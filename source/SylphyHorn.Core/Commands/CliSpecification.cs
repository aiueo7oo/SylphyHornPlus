using System.Runtime.Serialization;

namespace SylphyHorn.Commands
{
	[DataContract]
	internal sealed class CliCommandSpec
	{
		[DataMember(Name = "name", EmitDefaultValue = false)]
		public string Name;

		[DataMember(Name = "summary", EmitDefaultValue = false)]
		public string Summary;

		[DataMember(Name = "arguments", EmitDefaultValue = false)]
		public CliSpecArgument[] Arguments;

		[DataMember(Name = "constraints", EmitDefaultValue = false)]
		public CliSpecConstraint[] Constraints;

		[DataMember(Name = "notes", EmitDefaultValue = false)]
		public string[] Notes;

		[DataMember(Name = "effects", EmitDefaultValue = false)]
		public string[] Effects;

		[DataMember(Name = "prerequisites", EmitDefaultValue = false)]
		public string[] Prerequisites;

		[DataMember(Name = "resultFields", EmitDefaultValue = false)]
		public string[] ResultFields;

		[DataMember(Name = "examples", EmitDefaultValue = false)]
		public string[][] Examples;

		[DataMember(Name = "queries", EmitDefaultValue = false)]
		public string[][] Queries;
	}

	[DataContract]
	internal sealed class CliSpecArgument
	{
		[DataMember(Name = "name", EmitDefaultValue = false)]
		public string Name;

		[DataMember(Name = "type", EmitDefaultValue = false)]
		public string Type;

		[DataMember(Name = "required")]
		public bool Required;

		[DataMember(Name = "omission", EmitDefaultValue = false)]
		public string Omission;

		[DataMember(Name = "values", EmitDefaultValue = false)]
		public string[] Values;

		[DataMember(Name = "minimum", EmitDefaultValue = false)]
		public long? Minimum;

		[DataMember(Name = "maximum", EmitDefaultValue = false)]
		public long? Maximum;

		[DataMember(Name = "description", EmitDefaultValue = false)]
		public string Description;

		[DataMember(Name = "valuesFrom", EmitDefaultValue = false)]
		public string ValuesFrom;
	}

	[DataContract]
	internal sealed class CliSpecConstraint
	{
		[DataMember(Name = "kind", EmitDefaultValue = false)]
		public string Kind;

		[DataMember(Name = "arguments", EmitDefaultValue = false)]
		public string[] Arguments;

		[DataMember(Name = "whenPresent", EmitDefaultValue = false)]
		public string WhenPresent;
	}

	[DataContract]
	internal sealed class CliSpecResolution
	{
		[DataMember(Name = "status", EmitDefaultValue = false)]
		public string Status;

		[DataMember(Name = "observedAt", EmitDefaultValue = false)]
		public string ObservedAt;

		[DataMember(Name = "sources", EmitDefaultValue = false)]
		public CliSpecSource[] Sources;
	}

	[DataContract]
	internal sealed class CliSpecSource
	{
		[DataMember(Name = "args", EmitDefaultValue = false)]
		public string[] Args;

		[DataMember(Name = "response", EmitDefaultValue = false)]
		public CliResponse Response;
	}

	[DataContract]
	internal sealed class CliSpecField
	{
		[DataMember(Name = "name", EmitDefaultValue = false)]
		public string Name;

		[DataMember(Name = "type", EmitDefaultValue = false)]
		public string Type;

		[DataMember(Name = "optional")]
		public bool Optional;
	}

	[DataContract]
	internal sealed class CliSpecType
	{
		[DataMember(Name = "name", EmitDefaultValue = false)]
		public string Name;

		[DataMember(Name = "fields", EmitDefaultValue = false)]
		public CliSpecField[] Fields;
	}

	[DataContract]
	internal sealed class CliSpecError
	{
		[DataMember(Name = "code", EmitDefaultValue = false)]
		public string Code;

		[DataMember(Name = "exitCode")]
		public int ExitCode;

		[DataMember(Name = "action", EmitDefaultValue = false)]
		public string Action;
	}
}
