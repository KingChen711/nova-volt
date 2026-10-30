using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.FoundationTests.LabModel;

namespace Nvm.FoundationTests;

/// <summary>Gửi tin ra ngoài: đồng bộ chờ trả lời, bất đồng bộ không chờ; gửi ngay lúc gọi, ngoài transaction; adapter chọn nơi nhận.</summary>
public sealed class MessageBehaviorTests
{
    [Fact]
    public async Task Synchronous_send_without_a_gateway_is_an_error_the_handler_sees_and_asynchronous_send_only_leaves_a_trace()
    {
        var h = await FoundationHarness.CreateAsync();

        var sync = h.Run(new SendLabNotice { Synchronous = true });
        sync.Response!.Error!.ErrorMessage.ShouldContain("No client gateway");
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabTick>()}]").ShouldBe(0);

        h.Run(new SendLabNotice()).StatusCode.ShouldBe(200);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabTick>()}] WHERE Source = 'async'").ShouldBe(1);
        h.Traces.Records.ShouldContain(r => r.Level == TraceLevel.Warning && r.Message.StartsWith("SendMessage LabNotice", StringComparison.Ordinal));
        h.Traces.Records.ShouldContain(r => r.Level == TraceLevel.Warning && r.Message.StartsWith("SendMessageAsync LabNotice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Message_goes_out_when_sent_so_a_command_failing_afterwards_keeps_no_row_but_the_message_is_gone()
    {
        var h = await FoundationHarness.CreateAsync();
        var gateway = new RecordingGateway();
        h.Executor.Gateway = gateway;

        h.Run(new SendLabNotice { Synchronous = true }).StatusCode.ShouldBe(200);
        h.Scalar<string>($"SELECT Source FROM dbo.[{h.TableOf<LabTick>()}]").ShouldBe("reply");

        h.Run(new SendLabNotice { Synchronous = true, FailAfterSend = true }).StatusCode.ShouldBe(400);
        h.Run(new SendLabNotice { FailAfterSend = true }).StatusCode.ShouldBe(400);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabTick>()}]").ShouldBe(1);
        (gateway.Sent, gateway.Posted).ShouldBe((2, 1));
    }

    [Fact]
    public async Task Routing_gateway_sends_by_adapter_name_and_the_file_adapter_writes_one_file_per_message()
    {
        var h = await FoundationHarness.CreateAsync();
        var directory = Path.Combine(Path.GetTempPath(), "nvm-outbox-" + Guid.NewGuid().ToString("N"));
        var gateway = new RoutingMessageGateway();
        gateway.Adapters["LabAdapter"] = new FileMessageAdapter(directory, h.Time);
        h.Executor.Gateway = gateway;

        h.Run(new SendLabNotice()).StatusCode.ShouldBe(200);
        h.Run(new SendLabNotice { Synchronous = true }).StatusCode.ShouldBe(200);
        var files = Directory.GetFiles(directory);
        files.Length.ShouldBe(2);
        files.ShouldAllBe(f => Path.GetFileName(f).StartsWith("LabNotice_", StringComparison.Ordinal) && f.EndsWith(".json", StringComparison.Ordinal));
        (await File.ReadAllTextAsync(files[0], TestContext.Current.CancellationToken)).ShouldBe("""{"notice":1}""");

        h.Run(new SendLabNotice { Synchronous = true, Adapter = "Unknown" }).Response!.Error!.ErrorMessage.ShouldContain("Adapter Unknown is not configured");
        Directory.Delete(directory, recursive: true);
    }

    private sealed class RecordingGateway : IMessageGateway
    {
        public int Sent { get; private set; }

        public int Posted { get; private set; }

        public MessageReply Send(OutboundMessage message, TimeSpan timeout)
        {
            Sent++;
            return MessageReply.Of("reply");
        }

        public void Post(OutboundMessage message) => Posted++;
    }
}
