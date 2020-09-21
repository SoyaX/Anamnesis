﻿// Concept Matrix 3.
// Licensed under the MIT license.

namespace Anamnesis.Files.Infos
{
	using System;
	using System.IO;

	public abstract class FileInfoBase
	{
		public abstract string Extension { get; }
		public abstract string Name { get; }

		public abstract IFileSource FileSource { get; }

		public abstract FileBase DeserializeFile(Stream stream);
		public abstract void SerializeFile(FileBase file, Stream stream);

		public abstract bool IsFile(Type type);
	}
}
