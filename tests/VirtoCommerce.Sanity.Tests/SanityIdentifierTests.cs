using VirtoCommerce.Sanity.Core;
using Xunit;

namespace VirtoCommerce.Sanity.Tests;

[Trait("Category", "Unit")]
public class SanityIdentifierTests
{
    [Theory]
    [InlineData("page-1")]
    [InlineData("drafts.page-1")]
    [InlineData("8d6f8a3e-1b2c-4d5e-9f00-123456789abc")]
    [InlineData("image-abc123-800x600-jpg")]
    [InlineData("footerNavigation")]
    [InlineData("_hidden")]
    public void IsValid_SanityIdentifier_ReturnsTrue(string value)
    {
        Assert.True(SanityIdentifier.IsValid(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x\"] || _type == \"secret")]
    [InlineData("page 1")]
    [InlineData("page-1\n")]
    [InlineData("-page")]
    [InlineData(".page")]
    [InlineData("page/1")]
    public void IsValid_NotSanityIdentifier_ReturnsFalse(string value)
    {
        Assert.False(SanityIdentifier.IsValid(value));
    }
}
