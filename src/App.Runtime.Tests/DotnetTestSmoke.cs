using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IPhoneMirror.App.Runtime.Tests;

[TestClass]
public sealed class DotnetTestSmoke
{
    [TestMethod]
    public void TestAdapterDiscoversRuntimeTests()
    {
        Assert.IsTrue(typeof(IPhoneMirror.App.App).Assembly.GetName().Name
            ?.Equals("iPhoneMirror", StringComparison.OrdinalIgnoreCase) == true);
    }
}
