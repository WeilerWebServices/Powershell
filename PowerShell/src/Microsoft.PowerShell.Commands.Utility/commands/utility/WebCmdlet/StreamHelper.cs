// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.IO.Compression;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PowerShell.Commands
{
    /// <summary>
    /// Microsoft.PowerShell.Commands.WebResponse has a public property RawContentStream
    /// which is of type MemoryStream. We shipped like that in PowerShell 3. Creating
    /// this class as a wrapper to MemoryStream to lazily initialize. Otherwise, the
    /// content will unnecessarily be read even if there are no consumers for it.
    /// </summary>
    internal class WebResponseContentMemoryStream : MemoryStream
    {
        #region Data

        private Stream _originalStreamToProxy;
        private bool _isInitialized = false;
        private Cmdlet _ownerCmdlet;

        #endregion

        #region Constructors
        /// <summary>
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="initialCapacity"></param>
        /// <param name="cmdlet">Owner cmdlet if any.</param>
        internal WebResponseContentMemoryStream(Stream stream, int initialCapacity, Cmdlet cmdlet)
            : base(initialCapacity)
        {
            _originalStreamToProxy = stream;
            _ownerCmdlet = cmdlet;
        }
        #endregion

        /// <summary>
        /// </summary>
        public override bool CanRead
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// </summary>
        public override bool CanSeek
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// </summary>
        public override bool CanTimeout
        {
            get
            {
                return base.CanTimeout;
            }
        }

        /// <summary>
        /// </summary>
        public override bool CanWrite
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// </summary>
        public override long Length
        {
            get
            {
                Initialize();
                return base.Length;
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="destination"></param>
        /// <param name="bufferSize"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            Initialize();
            return base.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        /// <summary>
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            Initialize();
            return base.Read(buffer, offset, count);
        }

        /// <summary>
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Initialize();
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        /// <summary>
        /// </summary>
        /// <returns></returns>
        public override int ReadByte()
        {
            Initialize();
            return base.ReadByte();
        }

        /// <summary>
        /// </summary>
        /// <param name="value"></param>
        public override void SetLength(long value)
        {
            Initialize();
            base.SetLength(value);
        }

        /// <summary>
        /// </summary>
        /// <returns></returns>
        public override byte[] ToArray()
        {
            Initialize();
            return base.ToArray();
        }

        /// <summary>
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        public override void Write(byte[] buffer, int offset, int count)
        {
            Initialize();
            base.Write(buffer, offset, count);
        }

        /// <summary>
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Initialize();
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        /// <summary>
        /// </summary>
        /// <param name="value"></param>
        public override void WriteByte(byte value)
        {
            Initialize();
            base.WriteByte(value);
        }

        /// <summary>
        /// </summary>
        /// <param name="stream"></param>
        public override void WriteTo(Stream stream)
        {
            Initialize();
            base.WriteTo(stream);
        }

        /// <summary>
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        /// <summary>
        /// </summary>
        private void Initialize()
        {
            if (_isInitialized) { return; }

            _isInitialized = true;
            try
            {
                long totalLength = 0;
                byte[] buffer = new byte[StreamHelper.ChunkSize];
                ProgressRecord record = new ProgressRecord(StreamHelper.ActivityId, WebCmdletStrings.ReadResponseProgressActivity, "statusDescriptionPlaceholder");
                for (int read = 1; 0 < read; totalLength += read)
                {
                    if (_ownerCmdlet != null)
                    {
                        record.StatusDescription = StringUtil.Format(WebCmdletStrings.ReadResponseProgressStatus, totalLength);
                        _ownerCmdlet.WriteProgress(record);

                        if (_ownerCmdlet.IsStopping)
                        {
                            break;
                        }
                    }

                    read = _originalStreamToProxy.Read(buffer, 0, buffer.Length);

                    if (0 < read)
                    {
                        base.Write(buffer, 0, read);
                    }
                }

                if (_ownerCmdlet != null)
                {
                    record.StatusDescription = StringUtil.Format(WebCmdletStrings.ReadResponseComplete, totalLength);
                    record.RecordType = ProgressRecordType.Completed;
                    _ownerCmdlet.WriteProgress(record);
                }

                // make sure the length is set appropriately
                base.SetLength(totalLength);
                base.Seek(0, SeekOrigin.Begin);
            }
            catch (Exception)
            {
                base.Dispose();
                throw;
            }
        }
    }

    internal static class StreamHelper
    {
        #region Constants

        internal const int DefaultReadBuffer = 100000;

        internal const int ChunkSize = 10000;

        // just picked a random number
        internal const int ActivityId = 174593042;

        #endregion Constants

        #region Static Methods

        internal static void WriteToStream(Stream input, Stream output, PSCmdlet cmdlet, CancellationToken cancellationToken)
        {
            if (cmdlet == null)
            {
                throw new ArgumentNullException(nameof(cmdlet));
            }

            Task copyTask = input.CopyToAsync(output, cancellationToken);

            ProgressRecord record = new ProgressRecord(
                ActivityId,
                WebCmdletStrings.WriteRequestProgressActivity,
                WebCmdletStrings.WriteRequestProgressStatus);
            try
            {
                do
                {
                    record.StatusDescription = StringUtil.Format(WebCmdletStrings.WriteRequestProgressStatus, output.Position);
                    cmdlet.WriteProgress(record);

                    Task.Delay(1000).Wait(cancellationToken);
                }
                while (!copyTask.IsCompleted && !cancellationToken.IsCancellationRequested);

                if (copyTask.IsCompleted)
                {
                    record.StatusDescription = StringUtil.Format(WebCmdletStrings.WriteRequestComplete, output.Position);
                    cmdlet.WriteProgress(record);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Saves content from stream into filePath.
        /// Caller need to ensure <paramref name="stream"/> position is properly set.
        /// </summary>
        /// <param name="stream">Input stream.</param>
        /// <param name="filePath">Output file name.</param>
        /// <param name="cmdlet">Current cmdlet (Invoke-WebRequest or Invoke-RestMethod).</param>
        /// <param name="cancellationToken">CancellationToken to track the cmdlet cancellation.</param>
        internal static void SaveStreamToFile(Stream stream, string filePath, PSCmdlet cmdlet, CancellationToken cancellationToken)
        {
            // If the web cmdlet should resume, append the file instead of overwriting.
            FileMode fileMode = cmdlet is WebRequestPSCmdlet webCmdlet && webCmdlet.ShouldResume ? FileMode.Append : FileMode.Create;
            using FileStream output = new FileStream(filePath, fileMode, FileAccess.Write, FileShare.Read);
            WriteToStream(stream, output, cmdlet, cancellationToken);
        }

        private static string StreamToString(Stream stream, Encoding encoding)
        {
            StringBuilder result = new StringBuilder(capacity: ChunkSize);
            Decoder decoder = encoding.GetDecoder();

            int useBufferSize = 64;
            if (useBufferSize < encoding.GetMaxCharCount(10))
            {
                useBufferSize = encoding.GetMaxCharCount(10);
            }

            char[] chars = new char[useBufferSize];
            byte[] bytes = new byte[useBufferSize * 4];
            int bytesRead = 0;
            do
            {
                // Read at most the number of bytes that will fit in the input buffer. The
                // return value is the actual number of bytes read, or zero if no bytes remain.
                bytesRead = stream.Read(bytes, 0, useBufferSize * 4);

                bool completed = false;
                int byteIndex = 0;
                int bytesUsed;
                int charsUsed;

                while (!completed)
                {
                    // If this is the last input data, flush the decoder's internal buffer and state.
                    bool flush = (bytesRead == 0);
                    decoder.Convert(bytes, byteIndex, bytesRead - byteIndex,
                                    chars, 0, useBufferSize, flush,
                                    out bytesUsed, out charsUsed, out completed);

                    // The conversion produced the number of characters indicated by charsUsed. Write that number
                    // of characters to our result buffer
                    result.Append(chars, 0, charsUsed);

                    // Increment byteIndex to the next block of bytes in the input buffer, if any, to convert.
                    byteIndex += bytesUsed;

                    // The behavior of decoder.Convert changed start .NET 3.1-preview2.
                    // The change was made in https://github.com/dotnet/coreclr/pull/27229
                    // The recommendation from .NET team is to not check for 'completed' if 'flush' is false.
                    // Break out of the loop if all bytes have been read.
                    if (!flush && bytesRead == byteIndex)
                    {
                        break;
                    }
                }
            } while (bytesRead != 0);

            return result.ToString();
        }

        internal static string DecodeStream(Stream stream, string characterSet, out Encoding encoding)
        {
            try
            {
                encoding = Encoding.GetEncoding(characterSet);
            }
            catch (ArgumentException)
            {
                encoding = null;
            }

            return DecodeStream(stream, ref encoding);
        }

        internal static bool TryGetEncoding(string characterSet, out Encoding encoding)
        {
            bool result = false;
            try
            {
                encoding = Encoding.GetEncoding(characterSet);
                result = true;
            }
            catch (ArgumentException)
            {
                encoding = null;
            }

            return result;
        }

        private static readonly Regex s_metaexp = new Regex(
                @"<meta\s.*[^.><]*charset\s*=\s*[""'\n]?(?<charset>[A-Za-z].[^\s""'\n<>]*)[\s""'\n>]",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            );

        internal static string DecodeStream(Stream stream, ref Encoding encoding)
        {
            bool isDefaultEncoding = false;
            if (encoding == null)
            {
                // Use the default encoding if one wasn't provided
                encoding = ContentHelper.GetDefaultEncoding();
                isDefaultEncoding = true;
            }

            string content = StreamToString(stream, encoding);
            if (isDefaultEncoding)
            {
                do
                {
                    // check for a charset attribute on the meta element to override the default.
                    Match match = s_metaexp.Match(content);
                    if (match.Success)
                    {
                        Encoding localEncoding = null;
                        string characterSet = match.Groups["charset"].Value;

                        if (TryGetEncoding(characterSet, out localEncoding))
                        {
                            stream.Seek(0, SeekOrigin.Begin);
                            content = StreamToString(stream, localEncoding);
                            // report the encoding used.
                            encoding = localEncoding;
                        }
                    }
                } while (false);
            }

            return content;
        }

        internal static byte[] EncodeToBytes(string str, Encoding encoding)
        {
            if (encoding == null)
            {
                // just use the default encoding if one wasn't provided
                encoding = ContentHelper.GetDefaultEncoding();
            }

            return encoding.GetBytes(str);
        }

        internal static byte[] EncodeToBytes(string str)
        {
            return EncodeToBytes(str, null);
        }

        internal static Stream GetResponseStream(HttpResponseMessage response)
        {
            Stream responseStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            var contentEncoding = response.Content.Headers.ContentEncoding;

            // HttpClient by default will automatically decompress GZip and Deflate content.
            // We keep this decompression logic here just in case.
            if (contentEncoding != null && contentEncoding.Count > 0)
            {
                if (contentEncoding.Contains("gzip"))
                {
                    responseStream = new GZipStream(responseStream, CompressionMode.Decompress);
                }
                else if (contentEncoding.Contains("deflate"))
                {
                    responseStream = new DeflateStream(responseStream, CompressionMode.Decompress);
                }
            }

            return responseStream;
        }

        #endregion Static Methods
    }
}
