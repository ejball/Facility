﻿using System.Collections.Generic;

namespace Facility.Definition.Fsd
{
	internal sealed class FsdRemarksSection
	{
		public FsdRemarksSection(string name, IReadOnlyList<string> lines, NamedTextPosition position)
		{
			Name = name;
			Lines = lines;
			Position = position;
		}

		public string Name { get; }

		public IReadOnlyList<string> Lines { get; }

		public NamedTextPosition Position { get; }
	}
}
