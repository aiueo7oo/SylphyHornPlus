using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace SylphyHorn.Commands
{
	internal static class CliSpecService
	{
		internal static async Task<CliResponse> ExecuteAsync(string[] args, Func<string[], Task<CliResponse>> query)
		{
			if (args == null || args.Length == 0 || args[0] != "spec")
				return CliResponse.Fail("spec", "invalid_arguments", "Specify spec [COMMAND...] [--resolve].");

			var resolve = args.Length > 1 && args[args.Length - 1] == "--resolve";
			var words = args.Skip(1).Take(args.Length - 1 - (resolve ? 1 : 0)).ToArray();
			if (words.Any(word => string.IsNullOrWhiteSpace(word) || word.StartsWith("--", StringComparison.Ordinal)))
				return CliResponse.Fail("spec", "invalid_arguments", "Specify a command name followed by optional --resolve; do not pass command arguments.");
			if (words.Length == 0)
			{
				if (resolve) return CliResponse.Fail("spec", "invalid_arguments", "Select one command before --resolve.");
				return CliResponse.Ok("spec", new CliData
				{
					SpecVersion = 1,
					Commands = CliSpecCatalog.All.Select(item => new CliCommandSpec { Name = item.Name, Summary = item.Summary }).ToArray(),
				});
			}
			var target = string.Join(" ", words);
			var definition = CliSpecCatalog.Find(target);
			if (definition == null) return CliResponse.Fail("spec", "invalid_arguments", "Unknown command; use spec for the command list.");
			var data = new CliData
			{
				SpecVersion = 1, Target = target, Specification = definition,
				ResultSchema = DescribeResult(definition), Errors = ErrorsFor(target),
			};
			if (resolve)
			{
				var sources = new List<CliSpecSource>();
				foreach (var request in definition.Queries)
				{
					// Queries come only from the fixed read-only catalog, never from user-supplied arguments.
					CliResponse response;
					try
					{
						response = await query((string[])request.Clone());
						if (response == null || response.SchemaVersion != 1 || response.Command != CliCommand.Recognize(request)
							|| (response.Success ? response.Data == null || response.Error != null : response.Error == null || response.Data != null))
							response = CliResponse.Fail(CliCommand.Recognize(request), "result_unconfirmed", "Invalid source response.");
					}
					catch (Exception)
					{
						response = CliResponse.Fail(CliCommand.Recognize(request), "result_unconfirmed", "Source values could not be read.");
					}
					sources.Add(new CliSpecSource { Args = request, Response = response });
					// Avoid repeated connection timeouts when no host is available.
					if (!response.Success && response.Error?.Code == "host_unavailable") break;
				}
				var successCount = sources.Count(source => source.Response.Success);
				data.Resolution = new CliSpecResolution
				{
					Status = definition.Queries.Length == 0 ? "not-applicable"
						: successCount == definition.Queries.Length && sources.All(source => source.Response.Data.Complete != false) ? "complete" : successCount == 0 ? "unavailable" : "partial",
					ObservedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), Sources = sources.ToArray(),
				};
			}
			return CliResponse.Ok("spec", data);
		}

		private static readonly CliSpecError[] CommonErrors =
		{
			Error("invalid_arguments", "Exit 2. Correct arguments using this specification."),
			Error("host_unavailable", "Exit 3. The host may be absent or busy. Wait, then check that a compatible GUI host runs in the same user session and elevation level."),
			Error("launcher_failure", "Exit 4. The native launcher could not start the CLI or obtain its exit status. Check the installation; inspect state before retrying."),
			Error("response_too_large", "Exit 4. The response exceeded the transport limit. A mutation may have completed; query affected state before retrying."),
			Error("settings_save_failed", "Changes may be active in memory. Read current values and resolve the save failure before retrying."),
			Error("desktop_not_found", "Read desktop list and select a current destination."),
			Error("window_not_found", "Read window list; the window ID may have expired."),
			Error("window_changed", "Re-read the window instance and its location before retrying."),
			Error("window_pinned", "Inspect pin scope before explicitly unpinning or selecting another window."),
			Error("no_next_desktop", "Use --wrap if intended, or choose an existing destination."),
			Error("no_previous_desktop", "Use --wrap if intended, or choose an existing destination."),
			Error("no_last_used_desktop", "Select an explicit current desktop instead."),
			Error("monitor_unavailable", "Read monitor list and select a current monitor."),
			Error("state_unavailable", "Wait for the desktop provider to become ready and re-read state."),
			Error("operation_failed", "Inspect the error and current state before retrying."),
			Error("invalid_settings_file", "Check the XML backup format and file path."),
			Error("file_not_found", "Check the input path on this machine."),
			Error("app_id_unavailable", "Select an addressable application from app list or window list."),
			Error("host_busy", "Wait until editing/import/initialization completes, then read current state."),
			Error("unsupported", "Check OS capabilities and related settings; do not repeat unchanged."),
			Error("state_changed", "Re-read affected desktops, windows or rules, then reconsider the operation."),
			Error("result_unconfirmed", "Exit 5. A mutation may have happened. Read state before retrying; do not blindly repeat creation or toggles."),
			Error("partial_failure", "Inspect settings and per-item results; failure does not imply rollback."),
			Error("file_exists", "Choose another path or explicitly authorize --overwrite."),
			Error("elevation_required", "Task registration needs both GUI and CLI elevated; no automatic UAC."),
			Error("startup_target_mismatch", "Inspect existing registrations; this command will not repair or replace another target."),
			Error("startup_query_failed", "Registration could not be queried; do not treat it as absent."),
			Error("assignment_disabled", "Enable the selected rule explicitly before applying it."),
			Error("assignment_unavailable", "Read app assignment status; placement must be enabled and monitoring."),
			Error("assignment_failed", "Inspect error.results for each window before retrying."),
			Error("settings_unavailable", "The GUI settings window is unavailable in the current host configuration."),
			Error("assignment_not_found", "Read app assignment list and select a saved rule."),
			Error("assignment_conflict", "Inspect enabled rules for this app before explicitly disabling a conflicting rule."),
			Error("ambiguous_assignment", "Select a persisted rule ID from app assignment list."),
			Error("app_unavailable", "Read app list and select an assignable application identity."),
			Error("shortcut_conflict", "Inspect error.conflicts and change the requested binding or explicitly clear the conflicting binding."),
			Error("request_cancelled", "Request was cancelled before a confirmed result; inspect current state."),
		};

		private static CliSpecError Error(string code, string action)
			=> new CliSpecError
			{
				Code = code, Action = action,
				ExitCode = code == "invalid_arguments" ? 2 : code == "host_unavailable" ? 3 : code == "result_unconfirmed" ? 5 : 4,
			};

		private static CliSpecError[] ErrorsFor(string command)
		{
			var codes = new HashSet<string>(new[] { "invalid_arguments", "host_unavailable", "host_busy", "unsupported",
				"state_changed", "state_unavailable", "result_unconfirmed", "request_cancelled", "operation_failed",
				"response_too_large", "launcher_failure" });
			if (command.StartsWith("desktop ", StringComparison.Ordinal) || command == "window move")
				codes.UnionWith(new[] { "desktop_not_found", "no_next_desktop", "no_previous_desktop", "no_last_used_desktop" });
			if (command.StartsWith("window ", StringComparison.Ordinal))
				codes.UnionWith(new[] { "window_not_found", "window_changed", "window_pinned", "app_id_unavailable" });
			if (command.StartsWith("startup ", StringComparison.Ordinal))
				codes.UnionWith(new[] { "startup_target_mismatch", "startup_query_failed", "elevation_required" });
			if (command.StartsWith("settings ", StringComparison.Ordinal))
				codes.UnionWith(new[] { "file_exists", "file_not_found", "invalid_settings_file", "partial_failure" });
			if (command.StartsWith("app ", StringComparison.Ordinal) || command.StartsWith("desktop autoclose ", StringComparison.Ordinal))
				codes.UnionWith(new[] { "assignment_not_found", "assignment_conflict", "ambiguous_assignment", "app_unavailable",
					"assignment_disabled", "assignment_unavailable", "assignment_failed", "partial_failure" });
			if (command == "ui settings") codes.Add("settings_unavailable");
			if (command.StartsWith("shortcut ", StringComparison.Ordinal)) codes.Add("shortcut_conflict");
			if (command.StartsWith("notification ", StringComparison.Ordinal)) codes.Add("monitor_unavailable");
			if (command == "settings save" || command.EndsWith(" configure", StringComparison.Ordinal) || command.StartsWith("app ", StringComparison.Ordinal)
				|| command.StartsWith("shortcut ", StringComparison.Ordinal) || command.StartsWith("desktop autoclose ", StringComparison.Ordinal)
				|| command.StartsWith("desktop creation wallpaper ", StringComparison.Ordinal))
				codes.Add("settings_save_failed");
			return CommonErrors.Where(error => codes.Contains(error.Code)).ToArray();
		}

		private static CliSpecType[] DescribeResult(CliCommandSpec definition)
		{
			var result = new List<CliSpecType>();
			result.Add(new CliSpecType { Name = "response", Fields = Members(typeof(CliResponse)).Select(DescribeField).ToArray() });
			var pending = new Queue<Type>();
			var seen = new HashSet<Type>();
			var dataFields = Members(typeof(CliData)).Where(field => definition.ResultFields.Contains(Member(field).Name)).ToArray();
			if (dataFields.Length != definition.ResultFields.Length) throw new InvalidOperationException("Unknown result field in " + definition.Name);
			result.Add(new CliSpecType { Name = "CliData", Fields = dataFields.Select(DescribeField).ToArray() });
			foreach (var field in dataFields) Enqueue(field.FieldType, pending);
			pending.Enqueue(typeof(CliError));
			while (pending.Count != 0)
			{
				var type = pending.Dequeue();
				if (!seen.Add(type)) continue;
				var fields = Members(type);
				result.Add(new CliSpecType { Name = type.Name, Fields = fields.Select(DescribeField).ToArray() });
				foreach (var field in fields) Enqueue(field.FieldType, pending);
			}
			return result.ToArray();
		}

		private static FieldInfo[] Members(Type type) => type.GetFields().Where(field => Member(field) != null).ToArray();
		private static DataMemberAttribute Member(FieldInfo field) => field.GetCustomAttribute<DataMemberAttribute>();

		private static CliSpecField DescribeField(FieldInfo field)
			=> new CliSpecField { Name = Member(field).Name, Type = TypeName(field.FieldType), Optional = !Member(field).EmitDefaultValue };

		private static string TypeName(Type type)
		{
			if (type.IsArray) return TypeName(type.GetElementType()) + "[]";
			var underlying = Nullable.GetUnderlyingType(type);
			if (underlying != null) return TypeName(underlying) + "|null";
			if (type == typeof(string)) return "string|null";
			if (type == typeof(bool)) return "boolean";
			if (type == typeof(double) || type == typeof(float)) return "number";
			if (type.IsPrimitive) return "integer";
			return type.Name;
		}

		private static void Enqueue(Type type, Queue<Type> queue)
		{
			if (type.IsArray) type = type.GetElementType();
			if (type.GetCustomAttribute<DataContractAttribute>() != null) queue.Enqueue(type);
		}
	}
}
