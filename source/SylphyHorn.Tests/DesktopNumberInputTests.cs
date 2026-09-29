using System.Globalization;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class DesktopNumberInputTests
	{
		[Theory]
		[InlineData("2", "2")]
		[InlineData("\uFF12", "2")]
		[InlineData(" \uFF11\uFF12\u3000", "12")]
		[InlineData("\uFF12a", "2a")]
		[InlineData(null, null)]
		public void NormalizeConvertsFullWidthDigitsAndTrimsWhiteSpace(string text, string expected)
			=> Assert.Equal(expected, DesktopNumberInput.Normalize(text));

		[Theory]
		[InlineData("1", true)]
		[InlineData("100", true)]
		[InlineData("101", false)]
		[InlineData("0", false)]
		[InlineData("-1", false)]
		[InlineData(" 2", false)]
		[InlineData("\uFF12", false)]
		[InlineData("abc", false)]
		[InlineData(null, false)]
		public void TryParseAcceptsAsciiNumbersUpToTheMaximum(string text, bool accepted)
			=> Assert.Equal(accepted, DesktopNumberInput.TryParse(text, out _));

		[Theory]
		[InlineData("en")]
		[InlineData("ja")]
		public void InvalidNumberMessagesStateTheMaximum(string culture)
		{
			var maximum = DesktopNumberInput.Maximum.ToString(CultureInfo.InvariantCulture);
			foreach (var key in new[] { "Placement_InvalidNumber", "CreationWallpaper_InvalidNumber" })
			{
				Assert.Contains(maximum, Resources.ResourceManager.GetString(key, CultureInfo.GetCultureInfo(culture)));
			}
		}
	}
}
