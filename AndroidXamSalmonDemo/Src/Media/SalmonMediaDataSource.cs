/*
MIT License

Copyright (c) 2021 Max Kas

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
using Android.App;
using Android.Media;
using Android.Util;
using Android.Widget;
using Java.Lang;
using Java.Util.Concurrent;
using Salmon.Droid.Utils;
using Salmon.FS;
using Salmon.Streams;
using System;
using System.IO;
using System.Runtime.Remoting.Contexts;
using static Salmon.SalmonIntegrity;

namespace Salmon.Droid.Media
{
    /// <summary>
    /// Implementation of a MediaDataSource for encrypted content.
    /// This class provides a seekable source for the Android MediaPlayer
    /// </summary>
    public class SalmonMediaDataSource : MediaDataSource
    {
        private static readonly string TAG = typeof(SalmonMediaDataSource).Name;

        // Default cache buffer should be high enough for some mpeg videos to work
        // the cache buffers should be aligned to the SalmonFile chunk size for efficiency
        private const int DEFAULT_MEDIA_CACHE_BUFFER_SIZE = 2 * 1024 * 1024;
        // this offset is also needed to be aligned to the chunk size
        private static readonly int STREAM_OFFSET = 256 * 1024;
        // default threads is one but you can increase it
        private const int DEFAULT_MEDIA_THREADS = 1;

        private CacheBuffer[] buffers = null;
        private SalmonStream[] bufferedStreams;
        private int buffersCount = 2;
        private Activity activity;
        private SalmonFile salmonFile;
        private long streamSize;
        private int cacheBufferSize;
        private int threads = DEFAULT_MEDIA_THREADS;
        private IExecutorService executor;
        private bool integrityFailed;

        /// <summary>
        /// Construct a seekable source for the media player from an encrypted file source
        /// </summary>
        /// <param name="context">Context that this data source will be used with. This is usually the activity the MediaPlayer is attached to</param>
        /// <param name="salmonFile"></param>
        /// <param name="bufferSize"></param>
        /// <param name="threads"></param>
        public SalmonMediaDataSource(Activity activity, SalmonFile salmonFile,
            int bufferSize = DEFAULT_MEDIA_CACHE_BUFFER_SIZE, int threads = DEFAULT_MEDIA_THREADS)
        {
            this.activity = activity;
            this.salmonFile = salmonFile;
            this.streamSize = salmonFile.GetSize();
            this.cacheBufferSize = bufferSize;
            this.threads = threads;
            CreateBuffers();
            CreateStreams();
        }

        /// <summary>
        /// Method creates the parallel streams for reading from the file
        /// </summary>
        private void CreateStreams()
        {
            try
            {
                executor = Executors.NewFixedThreadPool(threads);
                bufferedStreams = new SalmonStream[threads];
                for (int i = 0; i < threads; i++)
                {
                    int threadBufferSize = (int)System.Math.Ceiling(cacheBufferSize / (double)threads);
                    bufferedStreams[i] = salmonFile.GetInputStream(threadBufferSize);
                }
            }
            catch (System.Exception ex)
            {
                Toast.MakeText(Application.Context, "Error: " + ex.Message, ToastLength.Long).Show();
            }
        }

        /// <summary>
        /// Create cache buffers that will be used for sourcing the files. 
        /// These will help reducing multiple small decryption reads from the encrypted source.
        /// The first buffer will be sourcing at the start of the media file where the header and indexing are
        /// The rest of the buffers can be placed to whatever position the user slides to
        /// </summary>
        private void CreateBuffers()
        {
            buffers = new CacheBuffer[buffersCount];
            for (int i = 0; i < buffers.Length; i++)
                buffers[i] = new CacheBuffer(cacheBufferSize);
        }

        /// <summary>
        /// Decrypts and reads the contents of an encrypted file
        /// </summary>
        /// <param name="position">The source file position the read will start from</param>
        /// <param name="buffer">The buffer that will store the decrypted contents</param>
        /// <param name="offset">The position on the buffer that the decrypted data will start</param>
        /// <param name="size">The length of the data requested</param>
        /// <returns></returns>
        public override int ReadAt(long position, byte[] buffer, int offset, int size)
        {
            if (position >= this.streamSize)
                return 0;
            int minSize = 0;
            int bytesRead = 0;
            try
            {
                CacheBuffer cacheBuffer = GetCacheBuffer(position);
                if (cacheBuffer == null)
                {
                    cacheBuffer = GetAvailCacheBuffer();
                    // for some media the player makes a second immediate request 
                    // in a position a few bytes before the first request. To make 
                    // sure we don't make 2 overlapping requests we start the buffer
                    // a little before to the first request.
                    long startPosition = position - STREAM_OFFSET;
                    if (startPosition < 0)
                        startPosition = 0;

                    bytesRead = FillBuffer(cacheBuffer, startPosition, offset, cacheBufferSize);

                    if (bytesRead <= 0)
                        return bytesRead;
                    cacheBuffer.startPos = startPosition;
                    cacheBuffer.count = bytesRead;
                }
                minSize = System.Math.Min(size, (int)(cacheBuffer.count - position + cacheBuffer.startPos));
                Array.Copy(cacheBuffer.buffer, (int)(position - cacheBuffer.startPos), buffer, 0, minSize);
            }
            catch (System.Exception ex)
            {
                ex.PrintStackTrace();
            }

            return minSize;
        }

        /// <summary>
        /// Fills a cache buffer with the decrypted data from the encrypted source file.
        /// </summary>
        /// <param name="cacheBuffer">The cache buffer that will store the decrypted contents</param>
        /// <param name="offset">The position on the buffer that the decrypted data will start</param>
        /// <param name="bufferSize">The length of the data requested</param>
        /// <returns></returns>
        private int FillBuffer(CacheBuffer cacheBuffer, long startPosition, int offset, int bufferSize)
        {
#if ENABLE_TIMING
        long start = SalmonTime.CurrentTimeMillis();
#endif
            int bytesRead = 0;
            if (threads == 0)
            {
                bytesRead = FillBufferPart(cacheBuffer, startPosition, offset, bufferSize, bufferedStreams[0]);
            }
            else
            {
                bytesRead = FillBufferMulti(cacheBuffer, startPosition, offset, bufferSize);
            }
#if ENABLE_TIMING
            Log.Debug(TAG, "Total requested: " + bufferSize + ", Total Read: " + bytesRead + " bytes in: " + (SalmonTime.CurrentTimeMillis() - start) + " ms");
#endif
            return bytesRead;
        }

        /// <summary>
        /// Fills a cache buffer with the decrypted data from a part of an encrypted file served as a salmon stream
        /// </summary>
        /// <param name="cacheBuffer">The cache buffer that will store the decrypted contents</param>
        /// <param name="offset">The position on the buffer that the decrypted data will start</param>
        /// <param name="bufferSize">The length of the data requested</param>
        /// /// <param name="salmonStream">The stream that will be used to read from</param>
        /// <returns></returns>
        private int FillBufferPart(CacheBuffer cacheBuffer, long start, int offset, int bufferSize,
            SalmonStream salmonStream)
        {
            // there is no need to pre test the SalmonFile for integrity we can start reading it
            // while we fill up our buffers. if we reach a chunk with a mismatch on the HMAC
            // there will be an integrity exception thrown.
            try
            {
                salmonStream.Seek(start, SeekOrigin.Begin);
                int totalBytesRead = salmonStream.Read(cacheBuffer.buffer, offset, bufferSize);
                return totalBytesRead;
            }
            catch (SalmonIntegrityException ex)
            {
                ex.PrintStackTrace();
                DisplayIntegrityErrorOnce();
            }
            catch (System.Exception ex)
            {
                ex.PrintStackTrace();
            }
            return 0;
        }

        /// <summary>
        /// Display a message when integrity test has failed
        /// </summary>
        private void DisplayIntegrityErrorOnce()
        {
            if (!integrityFailed)
            {
                integrityFailed = true;
                if (activity != null)
                {
                    activity.RunOnUiThread(new Runnable(() =>
                    {
                        Toast.MakeText(Application.Context, activity.GetString(Resource.String.FileCorrupOrTampered), ToastLength.Long).Show();
                    }));
                }

            }

        }

        /// <summary>
        /// Fill the buffer using parallel streams for performance
        /// </summary>
        /// <param name="cacheBuffer">The cache buffer that will store the decrypted data</param>
        /// <param name="startPosition">The source file position the read will start from</param>
        /// <param name="offset">The start position on the cache buffer that the decrypted data will be stored</param>
        /// <param name="bufferSize">The buffer size that will be used to read from the file</param>
        /// <returns></returns>
        private int FillBufferMulti(CacheBuffer cacheBuffer, long startPosition, int offset, int bufferSize)
        {
            int bytesRead = 0;
            // Multi threaded decryption jobs
            CountDownLatch countDownLatch = new CountDownLatch(threads);
            int partSize = (int)System.Math.Ceiling(bufferSize / (float)threads);
            for (int i = 0; i < threads; i++)
            {
                int index = i;
                executor.Submit(new Runnable(() =>
                {
                    int start = partSize * index;
                    int length = System.Math.Min(partSize, bufferSize - start);
                    int chunkBytesRead = FillBufferPart(cacheBuffer, startPosition + start, offset + start, length,
                        bufferedStreams[index]);
                    bytesRead += chunkBytesRead;
                    countDownLatch.CountDown();
                }));
            }
            countDownLatch.Await();
#if DEBUG
            System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create();
            byte[] hash = md5.ComputeHash(cacheBuffer.buffer, 0, bytesRead);
#endif
            return bytesRead;
        }

        /// <summary>
        /// Returns an available cache buffer if there is none then it reuses the last one
        /// </summary>
        /// <returns></returns>
        private CacheBuffer GetAvailCacheBuffer()
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                CacheBuffer buffer = buffers[i];
                if (buffer.count == 0)
                    return buffer;
            }
            return buffers[buffers.Length - 1];
        }

        /// <summary>
        /// Returns the buffer that contains the data requested.
        /// </summary>
        /// <param name="position">The source file position of the data to be read</param>
        /// <returns></returns>
        private CacheBuffer GetCacheBuffer(long position)
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                CacheBuffer buffer = buffers[i];
                if (position >= buffer.startPos && position < buffer.startPos + buffer.count)
                    return buffer;
            }
            return null;
        }

        public override long Size => salmonFile.GetSize();

        public override void Close()
        {
            CloseStreams();
            CLoseBuffers();
        }

        private void CLoseBuffers()
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] != null)
                    buffers[i].Clear();
                buffers[i] = null;
            }
        }

        private void CloseStreams()
        {
            for (int i = 0; i < threads; i++)
            {
                if (bufferedStreams[i] != null)
                    bufferedStreams[i].Close();
                bufferedStreams[i] = null;
            }
        }

        /// <summary>
        /// Class will be used to cache decrypted data that can later be read via the ReadAt() method
        /// without requesting frequent decryption reads.
        /// </summary>
        //TODO: replace the CacheBuffer with a MemoryStream to simplify the code
        public class CacheBuffer
        {
            public byte[] buffer = null;
            public long startPos = 0;
            public long count = 0;

            public CacheBuffer(int bufferSize)
            {
                buffer = new byte[bufferSize];
            }

            public void Clear()
            {
                if (buffer != null)
                    Array.Clear(buffer, 0, buffer.Length);
            }
        }
    }
}