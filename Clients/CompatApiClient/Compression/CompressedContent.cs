﻿using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace CompatApiClient.Compression
{
    public class CompressedContent : HttpContent
    {
        private readonly HttpContent content;
        private readonly ICompressor compressor;

        public CompressedContent(HttpContent content, ICompressor compressor)
        {
            this.content = content;
            this.compressor = compressor;
            AddHeaders();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var contentStream = await content.ReadAsStreamAsync().ConfigureAwait(false);
            var compressedLength = await compressor.CompressAsync(contentStream, stream).ConfigureAwait(false);
            Headers.ContentLength = compressedLength;
        }

        private void AddHeaders()
        {
            foreach (var (key, value) in content.Headers)
                Headers.TryAddWithoutValidation(key, value);
            Headers.ContentEncoding.Add(compressor.EncodingType);
            Headers.ContentLength = null;
        }
    }
}