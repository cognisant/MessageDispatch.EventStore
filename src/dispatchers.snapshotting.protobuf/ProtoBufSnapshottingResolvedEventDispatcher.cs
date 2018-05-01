﻿// <copyright file="ProtoBufSnapshottingResolvedEventDispatcher.cs" company="Cognisant">
// Copyright (c) Cognisant. All rights reserved.
// </copyright>

namespace CR.MessageDispatch.Dispatchers.Snapshotting.Protobuf
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using CR.MessageDispatch.Core;
    using EventStore.ClientAPI;
    using ProtoBuf;

    public class ProtoBufSnapshottingResolvedEventDispatcher : ISnapshottingDispatcher<ResolvedEvent>
    {
        private const string TempDirectoryName = "tmp/";
        private const int ChunkSize = 52428800;

        private readonly Func<IEnumerable<object>> _stateProvider;

        private string _snapshotBasePath;
        private int _catchupCheckpointCount;

        public ProtoBufSnapshottingResolvedEventDispatcher(Func<IEnumerable<object>> stateProvider, string snapshotBasePath, string snapshotVersion)
        {
            _stateProvider = stateProvider;
            _snapshotBasePath = snapshotBasePath;

            if (!Directory.Exists(_snapshotBasePath))
            {
                Directory.CreateDirectory(_snapshotBasePath);
            }

            _snapshotBasePath += $"/{snapshotVersion}/";

            if (!Directory.Exists(_snapshotBasePath))
            {
                Directory.CreateDirectory(_snapshotBasePath);
            }

            // delete any temp directories on startup
            if (Directory.Exists(_snapshotBasePath + TempDirectoryName))
            {
                Directory.Delete(_snapshotBasePath + TempDirectoryName, true);
            }
        }

        public IDispatcher<ResolvedEvent> InnerDispatcher { get; set; }

        public int? LoadCheckpoint()
        {
            var pos = GetHighestSnapshotPosition();
            return pos == -1 ? (int?)null : pos;
        }

        public IEnumerable<object> LoadObjects()
        {
            var pos = GetHighestSnapshotPosition();

            if (pos == -1)
            {
                yield break;
            }

            var path = _snapshotBasePath + GetHighestSnapshotPosition() + "/";

            var chunksRead = 0;

            while (true)
            {
                using (var retrieveStream = StreamForChunk(chunksRead, path, FileMode.Open))
                {
                    if (retrieveStream == null)
                    {
                        break;
                    }

                    var wrappedItems = Serializer.DeserializeItems<ItemWrapper>(retrieveStream, PrefixStyle.Base128, 0);

                    foreach (var item in wrappedItems)
                    {
                        yield return item.Item;
                    }
                }

                chunksRead++;
            }
        }

        public void Dispatch(ResolvedEvent message)
        {
            if (message.Event.EventType.Equals("CheckpointRequested"))
            {
                // checkpoint less often while catching up
                if (message.Event.Created < DateTime.Today)
                {
                    _catchupCheckpointCount++;
                    if (_catchupCheckpointCount % 30 == 0)
                    {
                        DoCheckpoint(message.OriginalEventNumber);
                        return;
                    }
                }
                else
                {
                    DoCheckpoint(message.OriginalEventNumber);
                    return;
                }
            }

            InnerDispatcher.Dispatch(message);
        }

        private int GetHighestSnapshotPosition()
        {
            var directories = Directory.GetDirectories(_snapshotBasePath);
            if (!directories.Any())
            {
                return -1;
            }

            return directories.Select(d => int.Parse(d.Replace(_snapshotBasePath, string.Empty))).Max();
        }

        private void DoCheckpoint(long eventNumber)
        {
            string tempPath = _snapshotBasePath + TempDirectoryName;
            Directory.CreateDirectory(tempPath);
            var itemEnumerable = _stateProvider();

            var chunkCount = 0;
            var didMoveNext = false;

            using (var enumerator = itemEnumerable.GetEnumerator())
            {
                do
                {
                    using (var serializeStream = StreamForChunk(chunkCount, tempPath, FileMode.Create))
                    {
                        while (serializeStream.Length <= ChunkSize)
                        {
                            didMoveNext = enumerator.MoveNext();
                            if (!didMoveNext)
                            {
                                break;
                            }

                            Serializer.SerializeWithLengthPrefix(serializeStream, new ItemWrapper { Item = enumerator.Current }, PrefixStyle.Base128, 0);
                        }

                        chunkCount++;
                    }
                }
                while (didMoveNext);
            }

            Directory.Move(tempPath, _snapshotBasePath + "/" + eventNumber);
        }

        private FileStream StreamForChunk(int chunkNumber, string basePath, FileMode mode)
        {
            var filePath = basePath + chunkNumber.ToString().PadLeft(5, '0') + ".chunk";

            if (mode == FileMode.Open && !File.Exists(filePath))
            {
                return null;
            }

            return new FileStream(filePath, mode);
        }

        [ProtoContract]
        private class ItemWrapper
        {
            [ProtoMember(1, DynamicType = true)]
            public object Item { get; set; }
        }
    }
}
