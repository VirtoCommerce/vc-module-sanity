using Xunit;

namespace VirtoCommerce.Sanity.Tests;

[Trait("Category", "Unit")]
public class Test
{
    [Fact]
    public void Run_Test()
    {
        Assert.Equal(0, 0);
    }
}
