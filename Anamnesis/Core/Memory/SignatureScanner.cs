﻿// Concept Matrix 3.
// Licensed under the MIT license.

namespace Anamnesis.Core.Memory
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Globalization;
	using System.Runtime.CompilerServices;
	using System.Runtime.InteropServices;
	using System.Text;
	using Anamnesis.Memory;

#pragma warning disable SA1011

	/// <summary>
	/// based on Dalamud's signature scanner: https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/SigScanner.cs.
	/// </summary>
	public sealed class SignatureScanner
	{
		/// <param name="module">The ProcessModule to be used for scanning.</param>
		public SignatureScanner(ProcessModule module)
		{
			this.Module = module;
			this.Is32BitProcess = !Environment.Is64BitProcess;

			// Limit the search space to .text section.
			this.SetupSearchSpace(module);

			Log.Write($"Module base: {this.TextSectionBase}");
			Log.Write($"Module size: {this.TextSectionSize}");
		}

		public bool IsCopy { get; private set; }
		public bool Is32BitProcess { get; }
		public IntPtr SearchBase => this.Module.BaseAddress;
		public IntPtr TextSectionBase => new IntPtr(this.SearchBase.ToInt64() + this.TextSectionOffset);
		public long TextSectionOffset { get; private set; }
		public int TextSectionSize { get; private set; }
		public IntPtr DataSectionBase => new IntPtr(this.SearchBase.ToInt64() + this.DataSectionOffset);
		public long DataSectionOffset { get; private set; }
		public int DataSectionSize { get; private set; }
		public ProcessModule Module { get; }

		private IntPtr TextSectionTop => this.TextSectionBase + this.TextSectionSize;

		/// <summary>
		/// Scan for a byte signature in the .text section.
		/// </summary>
		/// <param name="signature">The signature.</param>
		/// <returns>The real offset of the found signature.</returns>
		public IntPtr ScanText(string signature)
		{
			IntPtr mBase = this.TextSectionBase;
			IntPtr scanRet = this.Scan(mBase, this.TextSectionSize, signature);

			if (ReadByte(scanRet) == 0xE8)
				return this.ReadCallSig(scanRet);

			return scanRet;
		}

		/// <summary>
		/// Scan for a .data address using a .text function.
		/// This is intended to be used with IDA sigs.
		/// Place your cursor on the line calling a static address, and create and IDA sig.
		/// </summary>
		/// <param name="signature">The signature of the function using the data.</param>
		/// <param name="offset">The offset from function start of the instruction using the data.</param>
		/// <returns>An IntPtr to the static memory location.</returns>
		public IntPtr GetStaticAddressFromSig(string signature, int offset = 0)
		{
			IntPtr instrAddr = this.ScanText(signature);
			instrAddr = IntPtr.Add(instrAddr, offset);
			long bAddr = (long)this.Module.BaseAddress;
			long num;
			do
			{
				instrAddr = IntPtr.Add(instrAddr, 1);
				num = ReadInt32(instrAddr) + (long)instrAddr + 4 - bAddr;
			}
			while (!(num >= this.DataSectionOffset && num <= this.DataSectionOffset + this.DataSectionSize));
			return IntPtr.Add(instrAddr, ReadInt32(instrAddr) + 4);
		}

		/// <summary>
		/// Scan for a byte signature in the .data section.
		/// </summary>
		/// <param name="signature">The signature.</param>
		/// <returns>The real offset of the found signature.</returns>
		public IntPtr ScanData(string signature)
		{
			IntPtr scanRet = this.Scan(this.DataSectionBase, this.DataSectionSize, signature);
			return scanRet;
		}

		/// <summary>
		/// Scan for a byte signature in the whole module search area.
		/// </summary>
		/// <param name="signature">The signature.</param>
		/// <returns>The real offset of the found signature.</returns>
		public IntPtr ScanModule(string signature)
		{
			IntPtr scanRet = this.Scan(this.SearchBase, this.Module.ModuleMemorySize, signature);
			return scanRet;
		}

		public IntPtr Scan(IntPtr baseAddress, int size, string signature)
		{
			byte?[]? needle = this.SigToNeedle(signature);

			unsafe
			{
				long offset = 0;
				while (offset < size - needle.Length)
				{
					UIntPtr addr = new UIntPtr(Convert.ToUInt64(baseAddress.ToInt64() + offset));

					if (this.IsMatch(addr, needle))
						return (IntPtr)addr.ToPointer();

					// Advance an offset
					offset += 1;
				}
			}

			/*unsafe
			{
				byte* pCursor = (byte*)baseAddress.ToPointer();
				byte* pTop = (byte*)(baseAddress + size - needle.Length);
				while (pCursor < pTop)
				{
					if (this.IsMatch(pCursor, needle))
						return (IntPtr)pCursor;

					// Advance an offset
					pCursor += 1;
				}
			}*/

			throw new KeyNotFoundException($"Can't find a signature of {signature}");
		}

		public IntPtr ResolveRelativeAddress(IntPtr nextInstAddr, int relOffset)
		{
			if (this.Is32BitProcess)
				throw new NotSupportedException("32 bit is not supported.");

			return nextInstAddr + relOffset;
		}

		private static byte ReadByte(IntPtr baseAddress, int offset = 0)
		{
			UIntPtr addr = new UIntPtr(Convert.ToUInt64(baseAddress.ToInt64() + (long)offset));
			byte[] buffer = new byte[1];
			MemoryService.Read(addr, buffer, (UIntPtr)1, IntPtr.Zero);
			return buffer[0];
		}

		private static int ReadInt32(IntPtr baseAddress, int offset = 0)
		{
			UIntPtr addr = new UIntPtr(Convert.ToUInt64(baseAddress.ToInt64() + (long)offset));
			byte[] buffer = new byte[4];
			MemoryService.Read(addr, buffer, (UIntPtr)4, IntPtr.Zero);
			return BitConverter.ToInt32(buffer);
		}

		private static short ReadInt16(IntPtr baseAddress, int offset = 0)
		{
			UIntPtr addr = new UIntPtr(Convert.ToUInt64(baseAddress.ToInt64() + (long)offset));
			byte[] buffer = new byte[2];
			MemoryService.Read(addr, buffer, (UIntPtr)2, IntPtr.Zero);
			return BitConverter.ToInt16(buffer);
		}

		private static long ReadInt64(IntPtr baseAddress, int offset = 0)
		{
			UIntPtr addr = new UIntPtr(Convert.ToUInt64(baseAddress.ToInt64() + (long)offset));
			byte[] buffer = new byte[8];
			MemoryService.Read(addr, buffer, (UIntPtr)8, IntPtr.Zero);
			return BitConverter.ToInt64(buffer);
		}

		private void SetupSearchSpace(ProcessModule module)
		{
			IntPtr baseAddress = module.BaseAddress;

			// We don't want to read all of IMAGE_DOS_HEADER or IMAGE_NT_HEADER stuff so we cheat here.
			int ntNewOffset = ReadInt32(baseAddress, 0x3C);
			IntPtr ntHeader = baseAddress + ntNewOffset;

			// IMAGE_NT_HEADER
			IntPtr fileHeader = ntHeader + 4;
			short numSections = ReadInt16(ntHeader, 6);

			// IMAGE_OPTIONAL_HEADER
			IntPtr optionalHeader = fileHeader + 20;

			IntPtr sectionHeader;
			if (this.Is32BitProcess) // IMAGE_OPTIONAL_HEADER32
				sectionHeader = optionalHeader + 224;
			else // IMAGE_OPTIONAL_HEADER64
				sectionHeader = optionalHeader + 240;

			// IMAGE_SECTION_HEADER
			IntPtr sectionCursor = sectionHeader;
			for (int i = 0; i < numSections; i++)
			{
				long sectionName = ReadInt64(sectionCursor);

				// .text
				switch (sectionName)
				{
					case 0x747865742E: // .text
						this.TextSectionOffset = ReadInt32(sectionCursor, 12);
						this.TextSectionSize = ReadInt32(sectionCursor, 8);
						break;
					case 0x617461642E: // .data
						this.DataSectionOffset = ReadInt32(sectionCursor, 12);
						this.DataSectionSize = ReadInt32(sectionCursor, 8);
						break;
				}

				sectionCursor += 40;
			}
		}

		/// <summary>
		/// Helper for ScanText to get the correct address for
		/// IDA sigs that mark the first CALL location.
		/// </summary>
		/// <param name="sigLocation">The address the CALL sig resolved to.</param>
		/// <returns>The real offset of the signature.</returns>
		private IntPtr ReadCallSig(IntPtr sigLocation)
		{
			int jumpOffset = ReadInt32(IntPtr.Add(sigLocation, 1));
			return IntPtr.Add(sigLocation, 5 + jumpOffset);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private unsafe bool IsMatch(UIntPtr pCursor, byte?[] needle)
		{
			byte[] buffer = new byte[needle.Length];
			MemoryService.Read(pCursor, buffer, (UIntPtr)needle.Length, IntPtr.Zero);

			for (int i = 0; i < needle.Length; i++)
			{
				byte? expected = needle[i];
				if (expected == null)
					continue;

				byte actual = buffer[i];
				if (expected != actual)
					return false;
			}

			return true;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private byte?[] SigToNeedle(string signature)
		{
			// Strip all whitespaces
			signature = signature.Replace(" ", string.Empty);

			if (signature.Length % 2 != 0)
				throw new ArgumentException("Signature without whitespaces must be divisible by two.", nameof(signature));

			int needleLength = signature.Length / 2;
			byte?[]? needle = new byte?[needleLength];

			for (int i = 0; i < needleLength; i++)
			{
				string? hexString = signature.Substring(i * 2, 2);
				if (hexString == "??" || hexString == "**")
				{
					needle[i] = null;
					continue;
				}

				needle[i] = byte.Parse(hexString, NumberStyles.AllowHexSpecifier);
			}

			return needle;
		}
	}
}
