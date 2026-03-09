using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace NatSelect.Config.Template;

public class ConfigTemplate
{
    [YamlMember(Alias = "node")]
    public NodeConfig Node { get; set; } = new();

    [YamlMember(Alias = "network")]
    public NetworkConfig Network { get; set; } = new();

    [YamlMember(Alias = "database")]
    public DatabaseConfig Database { get; set; } = new();

    // 对于经常变的数值，我们可以直接用 object 或 dynamic 接收，或者单独加载
    [YamlMember(Alias = "game_balance")]
    public object? GameBalance { get; set; }
}


public class NodeConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "default_node";

    [YamlMember(Alias = "actor_system")]
    public ActorSystemConfig ActorSystem { get; set; } = new();
}

public class ActorSystemConfig
{
    [YamlMember(Alias = "max_mailbox_size")]
    public int MaxMailboxSize { get; set; } = 10000;

    [YamlMember(Alias = "worker_threads")]
    public int WorkerThreads { get; set; } = Environment.ProcessorCount;
}

public class NetworkConfig
{
    [YamlMember(Alias = "listen_port")]
    public int ListenPort { get; set; } = 9000;

    [YamlMember(Alias = "max_connections")]
    public int MaxConnections { get; set; } = 10000;
}

public class DatabaseConfig
{
    [YamlMember(Alias = "connection_string")]
    public string ConnectionString { get; set; } = "";

    [YamlMember(Alias = "pool_size")]
    public int PoolSize { get; set; } = 10;
}