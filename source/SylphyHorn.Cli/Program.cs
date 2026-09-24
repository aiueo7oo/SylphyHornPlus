using System;
using System.IO;
using System.IO.Pipes;
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
			if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
			{
				Console.WriteLine("sylphyhorn-cli monitor list");
				Console.WriteLine("sylphyhorn-cli desktop settings");
				Console.WriteLine("sylphyhorn-cli desktop configure [--loop true|false] [--override-windows-shortcuts true|false] " +
					"[--per-desktop-wallpaper true|false] [--override-on-startup true|false]");
				Console.WriteLine("sylphyhorn-cli notification settings");
				Console.WriteLine("sylphyhorn-cli notification configure [--on-switch true|false] [--always-show true|false] [--duration-ms N] " +
					"[--simple true|false] [--use-desktop-name true|false] [--theme (apps | system | light | dark | accent)] " +
					"[--corners (square | rounded | small-rounded)] [--font-family FAMILY] [--header-font-size N] [--body-font-size N] " +
					"[--header-align (left | center | right)] [--body-align (left | center | right)] [--line-spacing N] " +
					"[--monitor (current | all | N)] [--placement (top-left | top-center | top-right | center-left | center | center-right | " +
					"bottom-left | bottom-center | bottom-right)] " +
					"[--offset-x N] [--offset-y N] [--min-width N] [--simple-min-width N] [--min-height N] " +
					"[--pin-min-width N] [--pin-offset-x N] [--pin-offset-y N]");
				Console.WriteLine("sylphyhorn-cli tray settings");
				Console.WriteLine("sylphyhorn-cli tray configure [--show-desktop true|false] [--current-number-only true|false]");
				Console.WriteLine("sylphyhorn-cli settings get");
				Console.WriteLine("sylphyhorn-cli settings configure --language (auto | en | ja)");
				Console.WriteLine("sylphyhorn-cli desktop list");
				Console.WriteLine("sylphyhorn-cli desktop autoclose list");
				Console.WriteLine("sylphyhorn-cli desktop autoclose add (--name NAME | --number N)");
				Console.WriteLine("sylphyhorn-cli desktop autoclose remove (--name NAME | --number N)");
				Console.WriteLine("sylphyhorn-cli desktop switch " +
					"(--number N | --name NAME | --id ID | --next | --previous | --last-used) [--wrap]");
				Console.WriteLine("sylphyhorn-cli desktop create [--name NAME] [--switch]");
				Console.WriteLine("sylphyhorn-cli desktop rename --id ID --name NAME");
				Console.WriteLine("sylphyhorn-cli desktop reorder --id ID --number N");
				Console.WriteLine("sylphyhorn-cli desktop delete (--id ID | --number N)");
				Console.WriteLine("sylphyhorn-cli desktop wallpaper (--id ID | --number N) (--path PATH | --position POSITION)");
				Console.WriteLine("sylphyhorn-cli window list");
				Console.WriteLine("sylphyhorn-cli window move --id ID " +
					"(--desktop-number N | --desktop-name NAME | --desktop-id ID | --desktop-next | " +
					"--desktop-previous | --desktop-last-used | --desktop-new) [--wrap] [--follow]");
				Console.WriteLine("sylphyhorn-cli window pin --id ID --scope (window | app)");
				Console.WriteLine("sylphyhorn-cli window unpin --id ID --scope (window | app)");
				Console.WriteLine("sylphyhorn-cli ui task-view");
				Console.WriteLine("sylphyhorn-cli ui window-switch");
				Console.WriteLine("sylphyhorn-cli ui settings");
				Console.WriteLine("sylphyhorn-cli ui notification-toggle");
				Console.WriteLine("sylphyhorn-cli app list [--source (registered | windows)]");
				Console.WriteLine("sylphyhorn-cli app assignment list");
				Console.WriteLine("sylphyhorn-cli app assignment status");
				Console.WriteLine("sylphyhorn-cli app assignment configure [--enabled true|false] " +
					"[--create-missing-desktops true|false] [--close-created-desktops true|false]");
				Console.WriteLine("sylphyhorn-cli app assignment enable --id RULE_ID");
				Console.WriteLine("sylphyhorn-cli app assignment disable --id RULE_ID");
				Console.WriteLine("sylphyhorn-cli app assignment set (--path PATH | --app-id APP_ID | --id RULE_ID) (--desktop-name NAME | --desktop-number N)");
				Console.WriteLine("sylphyhorn-cli app assignment remove (--path PATH | --id RULE_ID)");
				Console.WriteLine("sylphyhorn-cli app assignment apply (--path PATH | --id RULE_ID | --all) [--dry-run]");
				Console.WriteLine("Results are JSON. Desktop numbers start at 1. Start SylphyHorn in the same user session first.");
				return 0;
			}
			string operation = CliCommand.Recognize(args);
			CliResponse response;
			try
			{
				operation = CliCommand.Parse(args).Operation;
			}
			catch (ArgumentException ex)
			{
				return Print(CliResponse.Fail(operation, "invalid_arguments", ex.Message));
			}

			var assembly = Assembly.GetExecutingAssembly();
			var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>().Company;
			var product = assembly.GetCustomAttribute<AssemblyProductAttribute>().Product;
			using (var pipe = new NamedPipeClientStream(".", CliProtocol.PipeName(company, product), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
			using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(operation == "app assignment apply" ? 45 : 15)))
			{
				var submitted = false;
				try
				{
					await pipe.ConnectAsync(2000, deadline.Token);
					submitted = true;
					await CliProtocol.WriteAsync(pipe, new CliRequest { Args = args }, deadline.Token);
					response = await CliProtocol.ReadAsync<CliResponse>(pipe, deadline.Token);
					if (response == null || response.SchemaVersion != 1 || response.Command != operation
						|| (response.Success ? response.Data == null || response.Error != null : response.Error == null || response.Data != null))
						throw new InvalidDataException("Invalid host response.");
				}
				catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is TimeoutException || ex is OperationCanceledException
					|| ex is UnauthorizedAccessException || ex is System.Runtime.Serialization.SerializationException)
				{
					response = submitted
						? CliResponse.Fail(operation, "result_unconfirmed", "The host response could not be confirmed. Query current state before retrying.")
						: CliResponse.Fail(operation, "host_unavailable", "Cannot connect to SylphyHorn in this user session and elevation level.", true);
				}
			}
			return Print(response);
		}

		private static int Print(CliResponse response)
		{
			Console.WriteLine(Encoding.UTF8.GetString(CliProtocol.Serialize(response)));
			return response.ExitCode;
		}
	}
}
