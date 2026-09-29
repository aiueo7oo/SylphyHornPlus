using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SylphyHorn.Commands;

namespace SylphyHorn.Cli
{
	internal static class Program
	{
		private static async Task<int> Main(string[] args)
		{
			Console.OutputEncoding = new UTF8Encoding(false);
			if (args.Length == 1 && args[0] == "--version")
			{
				PrintLocalVersion();
				return 0;
			}
			if (args.Length == 1 && args[0] == "version")
			{
				return Print(CliVersionInfo.Combine(CliVersionInfo.Read(Assembly.GetExecutingAssembly()), await SendAsync(args)));
			}
			if (args.Length > 0 && args[0] == "spec")
			{
				return Print(await CliSpecService.ExecuteAsync(args, SendAsync));
			}
			if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
			{
				PrintHelp();
				return 0;
			}
			return Print(await SendAsync(args));
		}

		private static void PrintLocalVersion()
		{
			var version = CliVersionInfo.Read(Assembly.GetExecutingAssembly());
			var revision = version.Revision == null ? "" : " (" + version.Revision.Substring(0, 9) + ")";
			Console.WriteLine("sylphyhorn-cli " + version.Version + revision);
		}

		private static void PrintHelp()
		{
			Console.WriteLine("sylphyhorn-cli --version");
			Console.WriteLine("sylphyhorn-cli spec [COMMAND...] [--resolve]");
			foreach (var item in CliSpecCatalog.All)
			{
				Console.WriteLine("sylphyhorn-cli " + item.Name + " " + string.Join(" ", item.Arguments.Select(FormatUsage)));
				Console.WriteLine("  " + item.Summary);
			}
			Console.WriteLine("Use spec COMMAND for arguments, constraints, result fields and examples; add --resolve for current values.");
		}

		// Such as --number INTEGER, [--wrap] or [--scope (window|app)].
		private static string FormatUsage(CliSpecArgument argument)
		{
			var usage = argument.Name;
			if (argument.Type != "flag")
			{
				usage += " " + ValuePlaceholder(argument);
			}
			return argument.Required ? usage : "[" + usage + "]";
		}

		private static string ValuePlaceholder(CliSpecArgument argument)
			=> argument.Values == null ? argument.Type.ToUpperInvariant() : "(" + string.Join("|", argument.Values) + ")";

		private static async Task<CliResponse> SendAsync(string[] args)
		{
			string operation = CliCommand.Recognize(args);
			CliResponse response;
			try
			{
				var parsed = CliCommand.Parse(args);
				operation = parsed.Operation;
				if (parsed.FilePath != null)
				{
					args = (string[])args.Clone();
					for (var index = 2; index < args.Length; index++)
					{
						if (args[index] != "--path") continue;
						args[index + 1] = Path.GetFullPath(parsed.FilePath);
						break;
					}
				}
			}
			catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
			{
				return CliResponse.Fail(operation, "invalid_arguments", ex.Message);
			}

			var assembly = Assembly.GetExecutingAssembly();
			var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>().Company;
			var product = assembly.GetCustomAttribute<AssemblyProductAttribute>().Product;
			using (var pipe = new NamedPipeClientStream(".", CliProtocol.PipeName(company, product), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
			using (var deadline = new CancellationTokenSource(TimeoutFor(operation)))
			{
				var submitted = false;
				try
				{
					await pipe.ConnectAsync(2000, deadline.Token);
					submitted = true;
					await CliProtocol.WriteAsync(pipe, new CliRequest { Args = args }, deadline.Token);
					response = await CliProtocol.ReadAsync<CliResponse>(pipe, deadline.Token);
					if (operation == "version" && response?.SchemaVersion == 1 && response.Command == null
						&& !response.Success && response.Data == null && response.Error?.Code == "invalid_arguments")
					{
						return CliResponse.Fail(operation, "unsupported", "This host does not support version queries.");
					}
					if (!CliResponse.IsWellFormed(response, operation))
					{
						throw new InvalidDataException("Invalid host response.");
					}
				}
				catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is TimeoutException || ex is OperationCanceledException
					|| ex is UnauthorizedAccessException || ex is System.Runtime.Serialization.SerializationException)
				{
					response = submitted
						? CliResponse.Fail(operation, "result_unconfirmed", "The host response could not be confirmed. Query current state before retrying.")
						: CliResponse.Fail(operation, "host_unavailable", "Cannot connect to SylphyHorn. The host may be absent or busy; check the user session and elevation level.", true);
				}
			}
			return response;
		}

		private static TimeSpan TimeoutFor(string operation)
		{
			if (operation == "version") return TimeSpan.FromSeconds(2);
			var longRunning = operation == "app assignment apply" || operation == "settings import" || operation == "settings reset"
				|| operation.StartsWith("startup ", StringComparison.Ordinal);
			return TimeSpan.FromSeconds(longRunning ? 45 : 15);
		}

		private static int Print(CliResponse response)
		{
			Console.WriteLine(Encoding.UTF8.GetString(CliProtocol.Serialize(response)));
			return response.ExitCode;
		}
	}
}
