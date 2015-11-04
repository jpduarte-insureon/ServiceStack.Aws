﻿// Copyright (c) Service Stack LLC. All Rights Reserved.
// License: https://raw.github.com/ServiceStack/ServiceStack/master/license.txt

using System;
using System.IO;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using ServiceStack.IO;
using ServiceStack.VirtualPath;

namespace ServiceStack.Aws.S3
{
    public class S3VirtualFile : AbstractVirtualFileBase
    {
        private S3VirtualPathProvider PathProvider { get; set; }

        public IAmazonS3 Client
        {
            get { return PathProvider.AmazonS3; }
        }

        public string BucketName
        {
            get { return PathProvider.BucketName; }
        }

        public S3VirtualFile(S3VirtualPathProvider pathProvider, IVirtualDirectory directory)
            : base(pathProvider, directory)
        {
            this.PathProvider = pathProvider;
        }

        public string DirPath
        {
            get { return base.Directory.VirtualPath; }
        }

        public string FilePath { get; set; }

        public string ContentType { get; set; }

        public override string Name
        {
            get { return S3VirtualPathProvider.GetFileName(FilePath); }
        }

        public override string VirtualPath
        {
            get { return FilePath; }
        }

        public DateTime FileLastModified { get; set; }

        public override DateTime LastModified
        {
            get { return FileLastModified; }
        }

        public override long Length
        {
            get { return ContentLength; }
        }

        public long ContentLength { get; set; }

        public string Etag { get; set; }

        public Stream Stream { get; set; }

        public S3VirtualFile Init(GetObjectResponse response)
        {
            FilePath = response.Key;
            ContentType = response.Headers.ContentType;
            FileLastModified = response.LastModified;
            ContentLength = response.Headers.ContentLength;
            Etag = response.ETag;
            Stream = response.ResponseStream;
            return this;
        }

        public override Stream OpenRead()
        {
            if (Stream == null || !Stream.CanRead)
            {
                var response = Client.GetObject(new GetObjectRequest
                {
                    Key = FilePath,
                    BucketName = BucketName,
                });
                Init(response);
            }

            return Stream;
        }

        public override void Refresh()
        {
            try
            {
                var response = Client.GetObject(new GetObjectRequest
                {
                    Key = FilePath,
                    BucketName = BucketName,
                    EtagToNotMatch = Etag,
                });
                Init(response);
            }
            catch (AmazonS3Exception ex)
            {
                if (ex.StatusCode != HttpStatusCode.NotModified)
                    throw ex;
            }
        }
    }
}