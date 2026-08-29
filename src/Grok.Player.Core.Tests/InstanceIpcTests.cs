using System.IO.Pipes;
using System.Text;
using Grok.Player.Core.Launch;

namespace Grok.Player.Core.Tests;

public sealed class InstanceIpcTests
{
    [Fact]
    public void Existing_owner_is_not_reacquired_even_on_the_same_thread()
    {
        var name = "GrokPlayer.test." + Guid.NewGuid().ToString("N");
        Assert.True(InstanceIpc.TryOwn(out var first, name, name));
        using (first)
        {
            Assert.False(InstanceIpc.TryOwn(out var second, name, name));
            second.Dispose();
        }
        Assert.True(InstanceIpc.TryOwn(out var reopened, name, name));
        reopened.Dispose();
    }

    [Fact]
    public void Ending_the_creating_thread_does_not_abandon_the_running_instance()
    {
        var name = "GrokPlayer.test." + Guid.NewGuid().ToString("N");
        InstanceIpc? owner = null;
        var thread = new Thread(() => InstanceIpc.TryOwn(out owner, name, name));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        using (owner!)
        {
            Assert.False(InstanceIpc.TryOwn(out var second, name, name));
            second.Dispose();
        }
    }

    [Fact]
    public async Task Protocol_payload_is_delivered_to_existing_instance_even_before_ui_subscribes()
    {
        var name = "GrokPlayer.test." + Guid.NewGuid().ToString("N");
        Assert.True(InstanceIpc.TryOwn(out var owner, name, name));
        using (owner)
        {
            const string payload = "grokplayer://open?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DQtl8lJwbd4g&sub=en";
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(5000);
                await client.WriteAsync(Encoding.UTF8.GetBytes(payload));
            }
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Received += value => received.TrySetResult(value);
            Assert.Equal(payload, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }
}
