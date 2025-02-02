﻿// -----------------------------------------------------------------------
// <copyright file="Cluster.cs" company="Asynkron AB">
//      Copyright (C) 2015-2022 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Proto.Cluster.Gossip;
using Proto.Cluster.Identity;
using Proto.Cluster.Metrics;
using Proto.Cluster.PubSub;
using Proto.Cluster.Seed;
using Proto.Extensions;
using Proto.Remote;
using Proto.Utils;

namespace Proto.Cluster;

/// <summary>
/// The cluster extension for <see cref="ActorSystem"/>
/// </summary>
[PublicAPI]
public class Cluster : IActorSystemExtension<Cluster>
{
    private Dictionary<string, ActivatedClusterKind> _clusterKinds = new();
    private Func<IEnumerable<Measurement<long>>>? _clusterKindObserver;
    private Func<IEnumerable<Measurement<long>>>? _clusterMembersObserver;

    public Cluster(ActorSystem system, ClusterConfig config)
    {
        System = system;
        Config = config;

        system.Extensions.Register(this);

        //register cluster messages
        var serialization = system.Serialization();
        serialization.RegisterFileDescriptor(ClusterContractsReflection.Descriptor);
        serialization.RegisterFileDescriptor(GossipContractsReflection.Descriptor);
        serialization.RegisterFileDescriptor(PubSubContractsReflection.Descriptor);
        serialization.RegisterFileDescriptor(GrainContractsReflection.Descriptor);
        serialization.RegisterFileDescriptor(SeedContractsReflection.Descriptor);
        serialization.RegisterFileDescriptor(EmptyReflection.Descriptor);

        Gossip = new Gossiper(this);
        PidCache = new PidCache();
        _ = new PubSubExtension(this);

        if (System.Metrics.Enabled)
        {
            _clusterMembersObserver = () => new[]
                {new Measurement<long>(MemberList.GetAllMembers().Length, new("id", System.Id), new("address", System.Address))};
            ClusterMetrics.ClusterMembersCount.AddObserver(_clusterMembersObserver);
        }

        SubscribeToTopologyEvents();
    }

    public static ILogger Logger { get; } = Log.CreateLogger<Cluster>();

    public IClusterContext ClusterContext { get; private set; } = null!;

    public Gossiper Gossip { get; }

    /// <summary>
    /// Cluster config used by this cluster
    /// </summary>
    public ClusterConfig Config { get; }

    /// <summary>
    /// Actor system this cluster is running on
    /// </summary>
    public ActorSystem System { get; }

    public IRemote Remote { get; private set; } = null!;

    /// <summary>
    /// A list of known cluster members. See <see cref="Proto.Cluster.MemberList"/> for details
    /// </summary>
    public MemberList MemberList { get; private set; } = null!;

    internal IIdentityLookup IdentityLookup { get; set; } = null!;

    internal IClusterProvider Provider { get; set; } = null!;

    public PidCache PidCache { get; }

    private void SubscribeToTopologyEvents() =>
        System.EventStream.Subscribe<ClusterTopology>(e => {
                foreach (var member in e.Left)
                {
                    PidCache.RemoveByMember(member);
                }
            }
        );

    public string[] GetClusterKinds() => _clusterKinds.Keys.ToArray();

    /// <summary>
    /// Starts the cluster member
    /// </summary>
    public async Task StartMemberAsync()
    {
        await BeginStartAsync(false);
        //gossiper must be started whenever any topology events starts flowing
        await Gossip.StartAsync();
        MemberList.InitializeTopologyConsensus();
        await Provider.StartMemberAsync(this);
        Logger.LogInformation("Started as cluster member");
        await MemberList.Started;
        Logger.LogInformation("I see myself");
    }

    /// <summary>
    /// Start the cluster member in client mode. A client member will not spawn virtual actors, but can talk to other members.
    /// </summary>
    public async Task StartClientAsync()
    {
        await BeginStartAsync(true);
        await Provider.StartClientAsync(this);

        Logger.LogInformation("Started as cluster client");
    }

    private async Task BeginStartAsync(bool client)
    {
        InitClusterKinds(client);
        Provider = Config.ClusterProvider;
        //default to partition identity lookup
        IdentityLookup = Config.IdentityLookup;

        Remote = System.Extensions.GetRequired<IRemote>("Remote module must be configured when using cluster");
        await Remote.StartAsync();

        Logger.LogInformation("Starting");
        MemberList = new MemberList(this);
        ClusterContext = Config.ClusterContextProducer(this);

        var kinds = GetClusterKinds();
        await IdentityLookup.SetupAsync(this, kinds, client);
        InitIdentityProxy();
        await this.PubSub().StartAsync();
        InitPidCacheTimeouts();
    }

    private void InitPidCacheTimeouts()
    {
        if (Config.RemotePidCacheClearInterval > TimeSpan.Zero && Config.RemotePidCacheTimeToLive > TimeSpan.Zero)
        {
            _ = Task.Run(async () => {
                    while (!System.Shutdown.IsCancellationRequested)
                    {
                        await Task.Delay(Config.RemotePidCacheClearInterval, System.Shutdown);
                        PidCache.RemoveIdleRemoteProcessesOlderThan(Config.RemotePidCacheTimeToLive);
                    }
                }, System.Shutdown
            );
        }
    }

    private void InitClusterKinds(bool client)
    {
        foreach (var clusterKind in Config.ClusterKinds)
        {
            _clusterKinds.Add(clusterKind.Name, clusterKind.Build(this));
        }

        if(!client)
            EnsureTopicKindRegistered();

        if (System.Metrics.Enabled)
        {
            _clusterKindObserver = () =>
                _clusterKinds.Values
                    .Select(ck =>
                        new Measurement<long>(ck.Count, new("id", System.Id), new("address", System.Address), new("clusterkind", ck.Name))
                    );

            ClusterMetrics.VirtualActorsCount.AddObserver(_clusterKindObserver);
        }
    }

    private void EnsureTopicKindRegistered()
    {
        // make sure PubSub topic kind is registered if user did not provide a custom registration
        if (!_clusterKinds.ContainsKey(TopicActor.Kind))
        {
            var store = new EmptyKeyValueStore<Subscribers>();

            _clusterKinds.Add(
                TopicActor.Kind,
                new ClusterKind(TopicActor.Kind, Props.FromProducer(() => new TopicActor(store))).Build(this)
            );
        }
    }

    private void InitIdentityProxy()
        => System.Root.SpawnNamedSystem(Props.FromProducer(() => new IdentityActivatorProxy(this)), IdentityActivatorProxy.ActorName);

    /// <summary>
    /// Shuts down the cluster member, <see cref="Proto.Remote.IRemote"/> extensions and the <see cref="ActorSystem"/>
    /// </summary>
    /// <param name="graceful">When true, this operation will await the shutdown of virtual actors managed by this member.
    /// This flag is also used by some of the clustering providers to explicitly deregister the member. When the shutdown is ungraceful,
    /// the member would have to reach its TTL to be removed in those cases.</param>
    /// <param name="reason">Provide the reason for the shutdown, that can be used for diagnosing problems</param>
    public async Task ShutdownAsync(bool graceful = true, string reason = "")
    {
        await Gossip.SetStateAsync("cluster:left", new Empty());

        //TODO: improve later, await at least two gossip cycles

        await Task.Delay((int) Config.GossipInterval.TotalMilliseconds * 2);

        if (_clusterKindObserver != null)
        {
            ClusterMetrics.VirtualActorsCount.RemoveObserver(_clusterKindObserver);
            _clusterKindObserver = null;
        }

        if (_clusterMembersObserver != null)
        {
            ClusterMetrics.ClusterMembersCount.RemoveObserver(_clusterMembersObserver);
            _clusterMembersObserver = null;
        }

        await System.ShutdownAsync(reason);
        Logger.LogInformation("Stopping Cluster {Id}", System.Id);

        await Gossip.ShutdownAsync();
        if (graceful) await IdentityLookup.ShutdownAsync();
        await Config.ClusterProvider.ShutdownAsync(graceful);
        await Remote.ShutdownAsync(graceful);

        Logger.LogInformation("Stopped Cluster {Id}", System.Id);
    }

    /// <summary>
    /// Resolves cluster identity to a <see cref="PID"/>. The cluster identity will be activated if it is not already.
    /// </summary>
    /// <param name="clusterIdentity">Cluster identity</param>
    /// <param name="ct">Token to cancel the operation</param>
    /// <returns></returns>
    public Task<PID?> GetAsync(ClusterIdentity clusterIdentity, CancellationToken ct) => IdentityLookup.GetAsync(clusterIdentity, ct);

    /// <summary>
    /// Sends a request to a virtual actor.
    /// </summary>
    /// <param name="clusterIdentity">Cluster identity of the actor</param>
    /// <param name="message">Message to send</param>
    /// <param name="context">Sender context to send the message through</param>
    /// <param name="ct">Token to cancel the operation</param>
    /// <typeparam name="T">Expected response type</typeparam>
    /// <returns>Response of null if timed out</returns>
    public Task<T> RequestAsync<T>(ClusterIdentity clusterIdentity, object message, ISenderContext context, CancellationToken ct) =>
        ClusterContext.RequestAsync<T>(clusterIdentity, message, context, ct)!;

    public ActivatedClusterKind GetClusterKind(string kind)
    {
        if (!_clusterKinds.TryGetValue(kind, out var clusterKind))
            throw new ArgumentException($"No cluster kind '{kind}' was not found");

        return clusterKind;
    }

    public ActivatedClusterKind? TryGetClusterKind(string kind)
    {
        _clusterKinds.TryGetValue(kind, out var clusterKind);

        return clusterKind;
    }

    /// <summary>
    /// Gets cluster identity for specified identity and kind. <see cref="PID"/> is attached to this cluster identity if available in <see cref="PidCache"/>
    /// </summary>
    /// <param name="identity">Identity</param>
    /// <param name="kind">Cluster kidn</param>
    /// <returns></returns>
    public ClusterIdentity GetIdentity(string identity, string kind)
    {
        var id = new ClusterIdentity
        {
            Identity = identity,
            Kind = kind
        };

        if (PidCache.TryGet(id, out var pid))
        {
            id.CachedPid = pid;
        }

        return id;
    }
}