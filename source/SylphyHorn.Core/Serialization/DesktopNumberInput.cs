using System.Globalization;
using System.Text;

namespace SylphyHorn.Serialization
{
	// Desktop numbers entered for app placement, automatic closing and creation wallpapers.
	internal static class DesktopNumberInput
	{
		// Higher numbers are rejected as input: a mistyped number could otherwise ask for dozens of new desktops.
		internal const int Maximum = 100;

		// Converts full-width digits typed with an IME to ASCII digits and removes surrounding white space.
		// Other characters are kept, so invalid input is still shown as typed.
		internal static string Normalize(string text)
		{
			if (text == null) return null;
			var builder = new StringBuilder(text.Trim());
			for (var index = 0; index < builder.Length; index++)
			{
				var character = builder[index];
				if (character >= '\uFF10' && character <= '\uFF19')
				{
					builder[index] = (char)('0' + (character - '\uFF10'));
				}
			}
			return builder.ToString();
		}

		// Accepts ASCII digits only, from 1 to Maximum; call Normalize first for text typed by the user.
		internal static bool TryParse(string text, out int number)
			=> int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= Maximum;
	}
}
