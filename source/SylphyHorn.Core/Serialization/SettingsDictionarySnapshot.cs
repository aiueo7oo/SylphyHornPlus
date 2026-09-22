using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MetroTrilithon.Serialization;
using SylphyHorn.AppPlacement;

namespace SylphyHorn.Serialization
{
	internal static class SettingsDictionarySnapshot
	{
	internal static Dictionary<string, object> CloneDictionary(IEnumerable<KeyValuePair<string, object>> source)
		=> source?.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value), StringComparer.Ordinal) ?? new Dictionary<string, object>(StringComparer.Ordinal);
		internal static object CloneValue(object value)
		{
			if (value == null || value is string || value.GetType().IsValueType) return value;
			// The entire placement graph is immutable, including its read-only rule list.
			if (value is AppPlacementConfiguration) return value;
			if (value is Array array) return array.Clone();
			if (value is IList<int> integers) return integers.ToList();
			if (value is IList<string> strings) return strings.ToList();
			if (value is IList<byte> bytes) return bytes.ToList();
			if (value is IList list)
			{
				var copy = new ArrayList(list.Count);
				foreach (var item in list) copy.Add(CloneValue(item));
				return copy;
			}
			return value;
		}

		internal static string ComputeFingerprint(IEnumerable<KeyValuePair<string, object>> settings)
	{
		var builder = new StringBuilder();
		foreach (var pair in settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			builder.Append(pair.Key.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(pair.Key).Append('=');
			AppendValue(builder, pair.Value);
			builder.Append(';');
		}
		using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).Replace("-", string.Empty).ToLowerInvariant();
	}

		private static void AppendValue(StringBuilder builder, object value)
		{
			if (value == null)
			{
				builder.Append("null");
				return;
			}
			builder.Append(value.GetType().FullName).Append(':');
			if (value is AppPlacementConfiguration placement)
			{
				AppendValue(builder, placement.Enabled);
				AppendValue(builder, placement.CreateMissingDesktops);
				builder.Append('[');
				foreach (var rule in placement.Rules)
				{
					AppendValue(builder, rule.Id);
					AppendValue(builder, rule.Enabled);
					AppendValue(builder, rule.App.Kind);
					AppendValue(builder, rule.App.Value);
					AppendValue(builder, rule.Destination.Kind);
					AppendValue(builder, rule.Destination.Name);
					AppendValue(builder, rule.Destination.Number);
					AppendValue(builder, rule.DisplayName);
					AppendValue(builder, rule.DisplayExecutablePath);
					builder.Append(';');
				}
				builder.Append(']');
				return;
			}
			if (value is string text)
			{
				builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);
				return;
			}
			if (value is IEnumerable enumerable && !(value is string))
			{
				builder.Append('[');
				foreach (var item in enumerable)
				{
					AppendValue(builder, item);
					builder.Append(',');
				}
				builder.Append(']');
				return;
			}
			builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
		}
	}
}
