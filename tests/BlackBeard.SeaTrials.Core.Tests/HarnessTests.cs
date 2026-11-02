namespace BlackBeard.SeaTrials.Core.Tests;

public class HarnessTests
{
    [Fact]
    public void TheTestRunnerRunsAndReachesCore()
    {
        CoreAssembly.Name.ShouldBe("BlackBeard.SeaTrials.Core");
    }
}
