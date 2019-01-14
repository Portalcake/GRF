﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ionic.Zlib;

namespace GRF
{
    public class Grf
    {
        private GrfHeader Header { get; } = new GrfHeader();

        public string Signature => Header.Signature;
        public List<GrfEntry> Entries { get; private set; } = new List<GrfEntry>();
        public int EntryCount => Entries.Count;
        public List<string> EntryNames => Entries.ConvertAll( f => f.Path );
        public string FilePath { get; private set; }
        public bool IsLoaded { get; private set; }

        public Grf() { }
        public Grf( string grfFilePath ) => Load( grfFilePath );

        public void Load( string grfFilePath )
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var fileInfo = new FileInfo( Path.Combine( baseDirectory, grfFilePath ) );
            FilePath = fileInfo.FullName;
            if( !fileInfo.Exists )
                throw new FileNotFoundException( grfFilePath );

            using( var fileStream = fileInfo.OpenRead() )
            using( var binaryReader = new BinaryReader( fileStream ) )
            {
                Header.Signature = Encoding.ASCII.GetString( binaryReader.ReadBytes( 16 ), 0, 15 );
                Header.EncryptKey = Encoding.ASCII.GetString( binaryReader.ReadBytes( 14 ) );
                Header.FileOffset = binaryReader.ReadUInt32();
                Header.Seed = binaryReader.ReadUInt32();
                var distortedFileCount = binaryReader.ReadUInt32();
                Header.Version = (GrfFormat)binaryReader.ReadUInt32();

                binaryReader.BaseStream.Seek( Header.FileOffset, SeekOrigin.Current );

                if( Header.Version == GrfFormat.Version102 || Header.Version == GrfFormat.Version103 )
                {
                    Header.FileCount = distortedFileCount - 7 - Header.Seed;
                    LoadVersion1xx(
                        binaryReader,
                        Header.FileCount );
                }
                else if( Header.Version == GrfFormat.Version200 )
                {
                    Header.FileCount = distortedFileCount - 7;
                    LoadVersion2xx(
                        binaryReader,
                        Header.FileCount );
                }
                else
                {
                    throw new NotImplementedException( $"Version {Header.Version} of GRF files is currently not supported." );
                }

                IsLoaded = true;
            }
        }

        public void Unload()
        {
            Entries.Clear();
            FilePath = string.Empty;
            Header.Signature = string.Empty;
            IsLoaded = false;
        }

        public bool FindEntry( string entryName, out GrfEntry entry )
        {
            int hashCode = entryName.GetHashCode();
            entry = Entries.FirstOrDefault( x => x.GetHashCode() == hashCode );

            return !( entry is null );
        }

        private void LoadVersion1xx( BinaryReader streamReader, uint fileCount )
        {
            var bodySize = (uint)( streamReader.BaseStream.Length - streamReader.BaseStream.Position );
            var bodyData = streamReader.ReadBytes( (int)bodySize );

            using( var bodyStream = new MemoryStream( bodyData ) )
            using( var bodyReader = new BinaryReader( bodyStream ) )
            {
                for( int i = 0, fileEntryHeader = 0; i < fileCount; i++ )
                {
                    bodyReader.BaseStream.Seek( fileEntryHeader, SeekOrigin.Begin );
                    int nameLength = bodyReader.PeekChar() - 6;
                    int fileEntryData = fileEntryHeader + bodyReader.ReadInt32() + 4;

                    bodyReader.BaseStream.Seek( fileEntryHeader + 6, SeekOrigin.Begin );
                    var encodedName = bodyReader.ReadBytes( nameLength );
                    var fileName = DecodeFileName( encodedName.AsSpan() );

                    bodyReader.BaseStream.Seek( fileEntryData, SeekOrigin.Begin );
                    uint compressedFileSizeBase = bodyReader.ReadUInt32();
                    uint compressedFileSizeAligned = bodyReader.ReadUInt32() - 37579;
                    uint uncompressedFileSize = bodyReader.ReadUInt32();
                    uint compressedFileSize = compressedFileSizeBase - uncompressedFileSize - 715;
                    var fileFlags = (FileFlag)bodyReader.ReadByte();
                    fileFlags |= IsFullEncrypted( fileName )
                        ? FileFlag.Mixed
                        : FileFlag.DES;
                    uint fileDataOffset = bodyReader.ReadUInt32() + Header.Size;

                    // skip directories and files with zero size
                    if( !fileFlags.HasFlag( FileFlag.File ) || uncompressedFileSize == 0 )
                        continue;

                    streamReader.BaseStream.Seek( fileDataOffset, SeekOrigin.Begin );

                    Entries.Add(
                        new GrfEntry(
                            fileName,
                            fileDataOffset,
                            compressedFileSize,
                            compressedFileSizeAligned,
                            uncompressedFileSize,
                            fileFlags,
                            this ) );

                    fileEntryHeader = fileEntryData + 17;
                }
            }
        }

        private void LoadVersion2xx( BinaryReader streamReader, uint fileCount )
        {
            var compressedBodySize = streamReader.ReadUInt32();
            var bodySize = streamReader.ReadUInt32();

            var compressedBody = streamReader.ReadBytes( (int)compressedBodySize );
            var bodyData = ZlibStream.UncompressBuffer( compressedBody );

            using( var bodyStream = new MemoryStream( bodyData ) )
            using( var bodyReader = new BinaryReader( bodyStream ) )
            {
                for( int i = 0; i < fileCount; i++ )
                {
                    var fileName = string.Empty;
                    char currentChar;
                    while( ( currentChar = (char)bodyReader.ReadByte() ) != 0 )
                    {
                        fileName += currentChar;
                    }

                    var compressedFileSize = bodyReader.ReadUInt32();
                    var compressedFileSizeAligned = bodyReader.ReadUInt32();
                    var uncompressedFileSize = bodyReader.ReadUInt32();
                    var fileFlags = (FileFlag)bodyReader.ReadByte();
                    var fileDataOffset = bodyReader.ReadUInt32();

                    // skip directories and files with zero size
                    if( !fileFlags.HasFlag( FileFlag.File ) || uncompressedFileSize == 0 )
                        continue;

                    Entries.Add(
                        new GrfEntry(
                            fileName,
                            Header.Size + fileDataOffset,
                            compressedFileSize,
                            compressedFileSizeAligned,
                            uncompressedFileSize,
                            fileFlags,
                            this ) );
                }
            }
        }

        private string DecodeFileName( Span<byte> encodedName )
        {
            for( int i = 0; i < encodedName.Length; i++ )
            {
                // swap nibbles
                encodedName[i] = (byte)( ( encodedName[i] & 0x0F ) << 4 | ( encodedName[i] & 0xF0 ) >> 4 );
            }

            for( int i = 0; i < encodedName.Length / DataEncryptionStandard.BlockSize; i++ )
            {
                DataEncryptionStandard.DecryptBlock( encodedName.Slice(
                    i * DataEncryptionStandard.BlockSize,
                    DataEncryptionStandard.BlockSize ) );
            }

            var fileName = string.Empty;
            for( int i = 0; i < encodedName.Length; i++ )
            {
                if( (char)encodedName[i] == 0 )
                    break;

                fileName += (char)encodedName[i];
            }

            return fileName;
        }

        private bool IsFullEncrypted( string fileName )
        {
            var extensions = new string[] { ".gnd", ".gat", ".act", ".str" };
            foreach( var extension in extensions )
            {
                if( fileName.EndsWith( extension ) )
                    return false;
            }

            return true;
        }

        internal byte[] GetCompressedBytes( uint offset, uint lenght )
        {
            using( var stream = new FileStream( FilePath, FileMode.Open ) )
            using( var binaryReader = new BinaryReader( stream ) )
            {
                binaryReader.BaseStream.Seek( offset, SeekOrigin.Begin );
                return binaryReader.ReadBytes( (int)lenght );
            }
        }
    }
}
