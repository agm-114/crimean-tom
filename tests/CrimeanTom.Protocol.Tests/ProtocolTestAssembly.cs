using System.Runtime.CompilerServices;

using Grpc.Net.Client;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class ProtocolTestAssembly
{
    [ModuleInitializer]
    internal static void InitializeGrpcClient()
    {
        // Real-host stories launch and stop several child processes. Initialize
        // gRPC's reflection-backed protocol constants before that lifecycle work.
        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1");
    }
}
