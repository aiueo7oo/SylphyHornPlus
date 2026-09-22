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
				Console.WriteLine("sylphyhorn-cli desktop list");
				Console.WriteLine("sylphyhorn-cli desktop switch (--number N | --name NAME | --id ID | --next | --previous) [--wrap]");
				Console.WriteLine("sylphyhorn-cli desktop create [--name NAME]");
				Console.WriteLine("sylphyhorn-cli desktop rename --id ID --name NAME");
				Console.WriteLine("sylphyhorn-cli desktop reorder --id ID --number N");
				Console.WriteLine("sylphyhorn-cli desktop delete --id ID");
				Console.WriteLine("sylphyhorn-cli window list");
				Console.WriteLine("sylphyhorn-cli window move --id ID (--desktop-number N | --desktop-name NAME | --desktop-id ID) [--follow]");
				Console.WriteLine("sylphyhorn-cli window pin --id ID --scope (window | app)");
				Console.WriteLine("sylphyhorn-cli window unpin --id ID --scope (window | app)");
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
			using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
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
