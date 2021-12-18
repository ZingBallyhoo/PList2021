using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;

namespace PList2021
{
    public static class BinaryFormatParser
    {
        private static ReadOnlySpan<byte> s_bplistMagic => new[]
        {
            (byte)'b', (byte)'p', (byte)'l', (byte)'i', (byte)'s', (byte)'t'
        };
        
        private static ReadOnlySpan<byte> s_version00String => new[]
        {
            (byte)'0', (byte)'0'
        };

        private const byte kCFBinaryPlistMarkerNull = 0x00;
        private const byte kCFBinaryPlistMarkerFalse = 0x08;
        private const byte kCFBinaryPlistMarkerTrue = 0x09;
        private const byte kCFBinaryPlistMarkerFill = 0x0F;
        private const byte kCFBinaryPlistMarkerInt = 0x10;
        private const byte kCFBinaryPlistMarkerReal = 0x20;
        private const byte kCFBinaryPlistMarkerDate = 0x33;
        private const byte kCFBinaryPlistMarkerData = 0x40;
        private const byte kCFBinaryPlistMarkerASCIIString = 0x50;
        private const byte kCFBinaryPlistMarkerUnicode16String = 0x60;
        private const byte kCFBinaryPlistMarkerUID = 0x80;
        private const byte kCFBinaryPlistMarkerArray = 0xA0;
        private const byte kCFBinaryPlistMarkerSet = 0xC0;
        private const byte kCFBinaryPlistMarkerDict = 0xD0;

        public static bool s_poolStrings = true;

        private unsafe struct Trailer
        {
            public fixed byte m_unused[5];
            public byte m_sortVersion;
            public byte m_offsetIntSize;
            public byte m_objectRefSize;
            public ulong m_numObjects;
            public ulong m_topObject;
            public ulong m_offsetTableOffset;
        }

        private readonly struct NodeTag
        {
            public readonly byte m_byte;
            
            public byte Lower => (byte)(m_byte & 15);
            public byte Upper => (byte)(m_byte & 0xF0);

            public ulong GetCount(ref ReadOnlySpan<byte> nodeDataSpan)
            {
                var lower = Lower;
                if (lower != 0xF) return lower;

                var intTag = MemoryMarshal.Read<NodeTag>(nodeDataSpan);
                if (intTag.Upper != kCFBinaryPlistMarkerInt) throw new InvalidDataException();
                var intByteCount = 1 << intTag.Lower;
                var intValue = ReadSizedUInt(nodeDataSpan.Slice(1), intByteCount);

                nodeDataSpan = nodeDataSpan.Slice(1 + intByteCount);

                return intValue;
            }
        }
        
        private static void ValidateHeader(ReadOnlySpan<byte> data)
        {
            var stringBytes = data.Slice(0, s_bplistMagic.Length);
            if (!stringBytes.SequenceEqual(s_bplistMagic))
            {
                throw new InvalidDataException("bad header");
            }
            var versionString = data.Slice(s_bplistMagic.Length, 2);
            if (!versionString.SequenceEqual(s_version00String))
            {
                throw new InvalidDataException("only supports version 00");
            }
        }
        
        public static unsafe object? Parse(ReadOnlySpan<byte> data)
        {
            ValidateHeader(data);

            Debug.Assert(sizeof(Trailer) == 32);
            var plistTrailer = MemoryMarshal.Read<Trailer>(data.Slice(data.Length - 32, 32));
            if (BitConverter.IsLittleEndian)
            {
                plistTrailer.m_numObjects = BinaryPrimitives.ReverseEndianness(plistTrailer.m_numObjects);
                plistTrailer.m_topObject = BinaryPrimitives.ReverseEndianness(plistTrailer.m_topObject);
                plistTrailer.m_offsetTableOffset = BinaryPrimitives.ReverseEndianness(plistTrailer.m_offsetTableOffset);
            }

            return ReadNode(plistTrailer, data, plistTrailer.m_topObject);
        }
        
        private static unsafe string DecodeString(ReadOnlySpan<byte> span, Encoding encoding)
        {
            if (span.IsEmpty)
            {
                return string.Empty;
            }

            var maxLength = encoding.GetMaxCharCount(span.Length);
            using var buffer = SpanOwner<char>.Allocate(maxLength);

            fixed (byte* source = span)
            fixed (char* destination = &buffer.DangerousGetReference())
            {
                var effectiveLength = encoding.GetChars(source, span.Length, destination, maxLength);

                var str = new ReadOnlySpan<char>(destination, effectiveLength);
                return str.ToString();
            }
        }

        private static string ReadString(ReadOnlySpan<byte> nodeSpan, ulong count, Encoding encoding)
        {
            Debug.Assert(count <= int.MaxValue);
            var stringBytes = nodeSpan.Slice(0, (int) count);
            if (s_poolStrings)
            {
                var str = StringPool.Shared.GetOrAdd(stringBytes, encoding);
                return str;
            } else
            {
                return DecodeString(stringBytes, encoding);
            }
        }

        private static object? ReadNode(in Trailer plistTrailer, ReadOnlySpan<byte> data, ulong nodeIndex64)
        {
            Debug.Assert(plistTrailer.m_offsetTableOffset <= int.MaxValue);
            var nodeOffsetArraySpan = data.Slice((int)plistTrailer.m_offsetTableOffset);

            Debug.Assert(nodeIndex64 <= int.MaxValue);
            var nodeIndex = (int) nodeIndex64;
            var thisNodeOffsetSpan = nodeOffsetArraySpan.Slice(nodeIndex * plistTrailer.m_offsetIntSize, plistTrailer.m_offsetIntSize);
            var thisNodeOffset = ReadSizedUInt(thisNodeOffsetSpan, plistTrailer.m_offsetIntSize);
            
            Debug.Assert(thisNodeOffset <= int.MaxValue);
            var nodeSpan = data.Slice((int)thisNodeOffset);
            var nodeTag = MemoryMarshal.Read<NodeTag>(nodeSpan);

            nodeSpan = nodeSpan.Slice(1);

            switch (nodeTag.Upper)
            {
                case kCFBinaryPlistMarkerNull:
                {
                    switch (nodeTag.Lower)
                    {
                        case kCFBinaryPlistMarkerNull: return null;
                        case kCFBinaryPlistMarkerFalse: return false;
                        case kCFBinaryPlistMarkerTrue: return true;
                        default: throw new InvalidDataException();
                    }
                }
                case kCFBinaryPlistMarkerInt:
                {
                    var intType = nodeTag.Lower;
                    var intSize = 1 << intType;

                    var intValue = ReadInt(nodeSpan, intSize);
                    return intValue;
                }
                case kCFBinaryPlistMarkerReal:
                {
                    switch (nodeTag.Lower)
                    {
                        case 2: return BinaryPrimitives.ReadSingleBigEndian(nodeSpan);
                        case 3: return BinaryPrimitives.ReadDoubleBigEndian(nodeSpan);
                        default: throw new InvalidDataException(nodeTag.Lower.ToString());
                    }
                }
                case kCFBinaryPlistMarkerDate & 0xf0:
                {
                    switch (nodeTag.m_byte)
                    {
                        case kCFBinaryPlistMarkerDate:
                        {
                            var length = nodeTag.GetCount(ref nodeSpan);
                            var ticks = length switch
                            {
                                2 => BinaryPrimitives.ReadSingleBigEndian(nodeSpan),
                                3 => BinaryPrimitives.ReadDoubleBigEndian(nodeSpan),
                                _ => throw new InvalidDataException("invalid date size")
                            };
                            return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ticks);
                        }
                        default: throw new InvalidDataException(nodeTag.m_byte.ToString());
                    }
                }
                case kCFBinaryPlistMarkerData:
                {
                    throw new NotImplementedException("kCFBinaryPlistMarkerData");
                }
                case kCFBinaryPlistMarkerASCIIString:
                {
                    var count = nodeTag.GetCount(ref nodeSpan);
                    var encoding = Encoding.ASCII;

                    return ReadString(nodeSpan, count, encoding);
                }
                case kCFBinaryPlistMarkerUnicode16String:
                {
                    var count = nodeTag.GetCount(ref nodeSpan) * 2;
                    var encoding = Encoding.BigEndianUnicode;

                    return ReadString(nodeSpan, count, encoding);
                }
                case kCFBinaryPlistMarkerUID:
                {
                    throw new NotImplementedException("kCFBinaryPlistMarkerUID");
                }
                case kCFBinaryPlistMarkerArray:
                case kCFBinaryPlistMarkerSet:
                {
                    var count = nodeTag.GetCount(ref nodeSpan);
                    Debug.Assert(count <= int.MaxValue);
                    
                    var indicesArrayByteSize = (int) count * plistTrailer.m_objectRefSize;
                    var keyIndicesSpan = nodeSpan.Slice(0, indicesArrayByteSize);

                    var outputArray = new object[count];

                    for (var j = 0; j < (int) count; j++)
                    {
                        var valueIndex = ReadSizedUInt(keyIndicesSpan.Slice(j * plistTrailer.m_objectRefSize), plistTrailer.m_objectRefSize);
                        var value = ReadNode(in plistTrailer, data, valueIndex);
                        outputArray[j] = value;
                    }

                    return outputArray;
                }
                case kCFBinaryPlistMarkerDict:
                {
                    var count = nodeTag.GetCount(ref nodeSpan);
                    Debug.Assert(count <= int.MaxValue);

                    var indicesArrayByteSize = (int) count * plistTrailer.m_objectRefSize;
                    var keyIndicesSpan = nodeSpan.Slice(0, indicesArrayByteSize);
                    var valueIndicesSpan = nodeSpan.Slice(indicesArrayByteSize, indicesArrayByteSize);

                    var outputDict = new Dictionary<string, object>((int)count);
                    
                    for (var j = 0; j < (int)count; j++)
                    {
                        var keyIndex = ReadSizedUInt(keyIndicesSpan.Slice(j * plistTrailer.m_objectRefSize), plistTrailer.m_objectRefSize);
                        var valueIndex = ReadSizedUInt(valueIndicesSpan.Slice(j * plistTrailer.m_objectRefSize), plistTrailer.m_objectRefSize);

                        var key = ReadNode(in plistTrailer, data, keyIndex);
                        var value = ReadNode(in plistTrailer, data, valueIndex);

                        if (key is not string keyString)
                        {
                            throw new InvalidDataException();
                        }
                        
                        outputDict[keyString] = value;
                    }
                    return outputDict;
                }
                default:
                {
                    throw new InvalidDataException(nodeTag.Upper.ToString());
                }
            }
        }

        private static long ReadInt(ReadOnlySpan<byte> data, int size)
        {
            // ---- from CFBinaryPList.c ----
            // in format version '00', 1, 2, and 4-byte integers have to be interpreted as unsigned,
            // whereas 8-byte integers are signed (and 16-byte when available)
            // negative 1, 2, 4-byte integers are always emitted as 8 bytes in format '00'
            // integers are not required to be in the most compact possible representation, but only the last 64 bits are significant currently
            
            Debug.Assert(data.Length >= size);
            return size switch
            {
                1 => data[0],
                2 => BinaryPrimitives.ReadUInt16BigEndian(data),
                4 => BinaryPrimitives.ReadUInt32BigEndian(data),
                8 => BinaryPrimitives.ReadInt64BigEndian(data),
                _ => throw new InvalidDataException(size.ToString())
            };
        }
        
        private static ulong ReadSizedUInt(ReadOnlySpan<byte> data, int size)
        {
            Debug.Assert(data.Length >= size);
            return size switch
            {
                1 => data[0],
                2 => BinaryPrimitives.ReadUInt16BigEndian(data),
                4 => BinaryPrimitives.ReadUInt32BigEndian(data),
                8 => BinaryPrimitives.ReadUInt64BigEndian(data),
                _ => throw new InvalidDataException(size.ToString())
            };
        }
    }
}