using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IPhoneMirror.App.Logic.Tests;

[TestClass]
public sealed class DotnetTestSmoke
{
    [TestMethod]
    public void TestAdapterDiscoversLogicTests()
    {
        Assert.IsTrue(
            IPhoneMirror.App.Services.HlsMediaPlaybackBridge.BuildArguments(
                new Uri("https://example.test/stream.m3u8"),
                new Uri("http://127.0.0.1:1/stream.ts"))
                .Contains("pipe:1", StringComparer.Ordinal));
    }
}
