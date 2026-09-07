using WheelWizard.Features.Patches;

namespace WheelWizard.Test.Features.Patches;

public class PatchConversionTests
{
    [Theory]
    [InlineData("./demolos01.brres", "demolos01.brres")]
    [InlineData("./button/timg/icon.tpl", "button/timg/icon.tpl")]
    [InlineData("button/timg/icon.tpl", "button/timg/icon.tpl")]
    [InlineData("././Race/Common.szs", "Race/Common.szs")]
    [InlineData("button\\timg\\icon.tpl", "button/timg/icon.tpl")]
    public void NormalizeArchiveLogicalPath_StripsDotSlashAndUnifiesSeparators(string input, string expected)
    {
        Assert.Equal(expected, PatchConversionHelpers.NormalizeArchiveLogicalPath(input));
    }
}
