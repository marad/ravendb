﻿// -----------------------------------------------------------------------
//  <copyright file="ManagedPrefixSlice.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Voron.Impl;
using Voron.Trees;

namespace Voron
{
	[StructLayout(LayoutKind.Explicit, Pack = 1)]
	public struct PrefixedSliceHeader
	{
		[FieldOffset(0)]
		public byte PrefixId;

		[FieldOffset(1)]
		public ushort PrefixUsage;

		[FieldOffset(3)]
		public ushort NonPrefixedDataSize;
	}

	public unsafe class PrefixedSlice : AbstractMemorySlice
	{
		public static PrefixedSlice AfterAllKeys = new PrefixedSlice(SliceOptions.AfterAllKeys);
		public static PrefixedSlice BeforeAllKeys = new PrefixedSlice(SliceOptions.BeforeAllKeys);
		public static PrefixedSlice Empty = new PrefixedSlice(Slice.Empty)
		{
			_size = 0,
			_keyLength = 0
		};

		public const byte NonPrefixedId = 0xff;

		private readonly PrefixedSliceHeader _header;
		private readonly Slice _nonPrefixedData;
		private ushort _size;
		private ushort _keyLength;

		private PrefixNode _prefix;

		public Slice NewPrefix = null;

		public PrefixedSlice()
		{
			Options = SliceOptions.Key;
			_size = 0;
			_keyLength = 0;
			_header = new PrefixedSliceHeader();
		}

		public PrefixedSlice(SliceOptions options)
			: this()
		{

			Options = options;
		}

		public PrefixedSlice(byte prefixId, ushort prefixUsage, Slice nonPrefixedValue)
		{
			_header = new PrefixedSliceHeader
			{
				PrefixId = prefixId,
				PrefixUsage = prefixUsage,
				NonPrefixedDataSize = nonPrefixedValue.KeyLength
			};

			_nonPrefixedData = nonPrefixedValue;
			_size = (ushort)(Constants.PrefixedSliceHeaderSize + nonPrefixedValue.KeyLength);
			_keyLength = (ushort)(prefixUsage + nonPrefixedValue.KeyLength);
			Options = nonPrefixedValue.Options;
		}

		public PrefixedSlice(NodeHeader* node)
		{
			if (node->KeySize > 0)
			{
				var prefixHeaderPtr = (PrefixedSliceHeader*)((byte*)node + Constants.NodeHeaderSize);
				_header = *prefixHeaderPtr;

				_nonPrefixedData = new Slice((byte*)prefixHeaderPtr + Constants.PrefixedSliceHeaderSize, _header.NonPrefixedDataSize);

				_size = node->KeySize;
				_keyLength = (ushort) (_header.PrefixUsage + _header.NonPrefixedDataSize);
			}
			else
			{
				_size = 0;
				_keyLength = 0;
			}

			Options = SliceOptions.Key;
		}

		public PrefixedSlice(IMemorySlice key)
		{
			_header = new PrefixedSliceHeader
			{
				PrefixId = NonPrefixedId,
				PrefixUsage = 0,
				NonPrefixedDataSize = key.KeyLength
			};

			_nonPrefixedData = key.ToSlice();

			_size = (ushort)(Constants.PrefixedSliceHeaderSize + key.KeyLength);
			_keyLength = key.KeyLength;
		}

		public PrefixedSliceHeader Header
		{
			get { return _header; }
		}

		public override ushort Size
		{
			get { return _size; }
		}

		public override ushort KeyLength
		{
			get { return _keyLength; }
		}

		public override void CopyTo(byte* dest)
		{
			var destHeader = (PrefixedSliceHeader*) dest;

			destHeader->PrefixId = _header.PrefixId;
			destHeader->PrefixUsage = _header.PrefixUsage;
			destHeader->NonPrefixedDataSize = _header.NonPrefixedDataSize;

			_nonPrefixedData.CopyTo(dest + Constants.PrefixedSliceHeaderSize);
		}

		internal void SetPrefix(PrefixNode prefix)
		{
			_prefix = prefix;
		}

		public override Slice ToSlice()
		{
			return Skip(0);
		}

		public override Slice Skip(ushort bytesToSkip)
		{
			if (_header.PrefixId == NonPrefixedId)
				return _nonPrefixedData.Skip(bytesToSkip);

			if (bytesToSkip == _header.PrefixUsage)
				return _nonPrefixedData;

			if (bytesToSkip > _header.PrefixUsage)
				return _nonPrefixedData.Skip((ushort)(bytesToSkip - _header.PrefixUsage));

			// bytesToSkip < _header.PrefixUsage

			Debug.Assert(_prefix != null);

			var prefixPart = _header.PrefixUsage - bytesToSkip;

			var sliceSize = prefixPart + _header.NonPrefixedDataSize;
			var sliceData = new byte[sliceSize];

			_prefix.Value.CopyTo(bytesToSkip, sliceData, 0, prefixPart);
			_nonPrefixedData.CopyTo(0, sliceData, prefixPart, sliceSize - prefixPart);

			return new Slice(sliceData);
		}

		protected override int CompareData(IMemorySlice other, SliceComparer cmp, ushort size)
		{
			var prefixedSlice = other as PrefixedSlice;

			if (prefixedSlice != null)
			{
				// compare prefixes
				var comparedPrefixBytes = Math.Min(_header.PrefixUsage, prefixedSlice._header.PrefixUsage);
				var r = ComparePrefixes(prefixedSlice, cmp, comparedPrefixBytes);

				if (r != 0)
					return r;

				// compare prefix and non prefix bytes
				size -= comparedPrefixBytes;

				if (_header.PrefixUsage > comparedPrefixBytes)
				{
					var remainingPrefix = Math.Min(Math.Min(_header.PrefixUsage - comparedPrefixBytes, prefixedSlice._header.NonPrefixedDataSize), size);

					r = ComparePrefixWithNonPrefixedData(prefixedSlice._nonPrefixedData, cmp, comparedPrefixBytes, remainingPrefix);

					if (r != 0)
						return r;

					// compare non prefixed data

					size -= (ushort)remainingPrefix;

					r = prefixedSlice.CompareNonPrefixedData(remainingPrefix, _nonPrefixedData, 0, cmp, size);

					return r * -1;
				}

				if (prefixedSlice._header.PrefixUsage > comparedPrefixBytes)
				{
					var remainingPrefix = Math.Min(Math.Min(prefixedSlice._header.PrefixUsage - comparedPrefixBytes, _header.NonPrefixedDataSize), size);

					r = prefixedSlice.ComparePrefixWithNonPrefixedData(_nonPrefixedData, cmp, comparedPrefixBytes, remainingPrefix);

					if (r != 0)
						return r * -1;

					// compare non prefixed data

					size -= (ushort)remainingPrefix;

					r = CompareNonPrefixedData(remainingPrefix, prefixedSlice._nonPrefixedData, 0, cmp, size);

					return r;
				}

				// both prefixes were equal, now compare non prefixed data

				r = CompareNonPrefixedData(0, prefixedSlice._nonPrefixedData, 0, cmp, size);

				return r;
			}

			var slice = other as Slice;

			if (slice != null)
			{
				var prefixLength = Math.Min(_header.PrefixUsage, size);

				var r = ComparePrefixWithNonPrefixedData(slice, cmp, 0, prefixLength);

				if (r != 0)
					return r;

				// compare non prefixed data

				size -= prefixLength;

				r = CompareNonPrefixedData(0, slice, prefixLength, cmp, size);

				return r;
			}

			throw new NotSupportedException("Cannot compare because of unknown slice type: " + other.GetType());
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private int ComparePrefixes(PrefixedSlice other, SliceComparer cmp, int count)
		{
			if (count == 0)
				return 0;

			return _prefix.Value.CompareSlices(other._prefix.Value, cmp, count);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal int ComparePrefixWithNonPrefixedData(Slice other, SliceComparer cmp, int prefixOffset, int count)
		{
			if (count == 0)
				return 0;

			Debug.Assert(_prefix != null);

			return _prefix.Value.CompareSlices(other, cmp, count, prefixOffset);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal int CompareNonPrefixedData(int offset, Slice other, int otherOffset, SliceComparer cmp, int count)
		{
			if (count == 0)
				return 0;

			return _nonPrefixedData.CompareSlices(other, cmp, count, offset, otherOffset);
		}

		public override string ToString()
		{
			if (_prefix != null)
				return new Slice(_prefix.Value, _header.PrefixUsage) + _nonPrefixedData.ToString();

			if (_header.PrefixId == NonPrefixedId)
				return _nonPrefixedData.ToString();

			return string.Format("prefix_id: {0} [usage: {1}], non_prefixed: {2}", _header.PrefixId, _header.PrefixUsage, _nonPrefixedData);
		}
	}
}