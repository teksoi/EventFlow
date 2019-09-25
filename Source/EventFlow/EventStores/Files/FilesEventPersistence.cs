﻿// The MIT License (MIT)
// 
// Copyright (c) 2015-2018 Rasmus Mikkelsen
// Copyright (c) 2015-2018 eBay Software Foundation
// https://github.com/eventflow/EventFlow
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventFlow.Core;
using EventFlow.Exceptions;
using EventFlow.Logs;

namespace EventFlow.EventStores.Files
{
    public class FilesEventPersistence : IEventPersistence
    {
        private readonly ILog _log;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IFilesEventLocator _filesEventLocator;
        private readonly AsyncLock _asyncLock = new AsyncLock();
        private readonly string _logFilePath;
        private long _globalSequenceNumber;
        private readonly Dictionary<long, string> _eventLog;

        public class FileEventData : ICommittedDomainEvent
        {
            public long GlobalSequenceNumber { get; }
            public string AggregateId { get; }
            public string Data { get; }
            public string Metadata { get; }
            public int AggregateSequenceNumber { get; }

            public FileEventData(
                long globalSequenceNumber,
                string aggregateId,
                string data,
                string metadata,
                int aggregateSequenceNumber)
            {
                GlobalSequenceNumber = globalSequenceNumber;
                AggregateId = aggregateId;
                Data = data;
                Metadata = metadata;
                AggregateSequenceNumber = aggregateSequenceNumber;
            }
        }

        public class EventStoreLog
        {
            public long GlobalSequenceNumber { get; }
            public Dictionary<long, string> Log { get; }

            public EventStoreLog(
                long globalSequenceNumber,
                Dictionary<long, string> log)
            {
                GlobalSequenceNumber = globalSequenceNumber;
                Log = log;
            }
        }

        public FilesEventPersistence(
            ILog log,
            IJsonSerializer jsonSerializer,
            IFilesEventStoreConfiguration configuration,
            IFilesEventLocator filesEventLocator)
        {
            _log = log;
            _jsonSerializer = jsonSerializer;
            _filesEventLocator = filesEventLocator;
            _logFilePath = Path.Combine(configuration.StorePath, "Log.store");

            if (File.Exists(_logFilePath))
            {
                var json = File.ReadAllText(_logFilePath);
                var eventStoreLog = _jsonSerializer.Deserialize<EventStoreLog>(json);
                _globalSequenceNumber = eventStoreLog.GlobalSequenceNumber;
                _eventLog = eventStoreLog.Log ?? new Dictionary<long, string>();

                if (_eventLog.Count != _globalSequenceNumber)
                {
                    eventStoreLog = RecreateEventStoreLog(configuration.StorePath);
                    _globalSequenceNumber = eventStoreLog.GlobalSequenceNumber;
                    _eventLog = eventStoreLog.Log;
                }
            }
            else
            {
                _eventLog = new Dictionary<long, string>();
            }
        }

        public async Task<AllCommittedEventsPage> LoadAllCommittedEvents(
            GlobalPosition globalPosition,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var startPosition = globalPosition.IsStart
                ? 1
                : int.Parse(globalPosition.Value);

            var committedDomainEvents = new List<FileEventData>();

            using (await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                var paths = EnumeratePaths(startPosition).Take(pageSize);

                foreach (var path in paths)
                {
                    var committedDomainEvent = await LoadFileEventDataFile(path).ConfigureAwait(false);
                    committedDomainEvents.Add(committedDomainEvent);
                }
            }

            var nextPosition = committedDomainEvents.Any()
                ? committedDomainEvents.Max(e => e.GlobalSequenceNumber) + 1
                : startPosition;

            return new AllCommittedEventsPage(new GlobalPosition(nextPosition.ToString()), committedDomainEvents);
        }

        private IEnumerable<string> EnumeratePaths(long startPosition)
        {
            while (_eventLog.TryGetValue(startPosition, out var path))
            {
                if (File.Exists(path))
                {
                    yield return path;
                }

                startPosition++;
            }
        }

        public async Task<IReadOnlyCollection<ICommittedDomainEvent>> CommitEventsAsync(
            IIdentity id,
            IReadOnlyCollection<SerializedEvent> serializedEvents,
            CancellationToken cancellationToken)
        {
            using (await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                var committedDomainEvents = new List<ICommittedDomainEvent>();

                var aggregatePath = _filesEventLocator.GetEntityPath(id);
                if (!Directory.Exists(aggregatePath))
                {
                    Directory.CreateDirectory(aggregatePath);
                }

                foreach (var serializedEvent in serializedEvents)
                {
                    var eventPath = _filesEventLocator.GetEventPath(id, serializedEvent.AggregateSequenceNumber);
                    _globalSequenceNumber++;
                    _eventLog[_globalSequenceNumber] = eventPath;

                    var fileEventData = new FileEventData(
                        _globalSequenceNumber,
                        id.Value,
                        serializedEvent.SerializedData,
                        serializedEvent.SerializedMetadata,
                        serializedEvent.AggregateSequenceNumber);

                    var json = _jsonSerializer.Serialize(fileEventData, true);

                    using (var streamWriter = CreateNewTextFile(eventPath, fileEventData))
                    {
                        _log.Verbose("Writing file '{0}'", eventPath);
                        await streamWriter.WriteAsync(json).ConfigureAwait(false);
                    }

                    committedDomainEvents.Add(fileEventData);
                }

                using (var streamWriter = File.CreateText(_logFilePath))
                {
                    _log.Verbose(
                        "Writing global sequence number '{0}' to '{1}'",
                        _globalSequenceNumber,
                        _logFilePath);
                    var json = _jsonSerializer.Serialize(
                        new EventStoreLog(_globalSequenceNumber, _eventLog),
                        true);
                    await streamWriter.WriteAsync(json).ConfigureAwait(false);
                }

                return committedDomainEvents;
            }
        }

        private StreamWriter CreateNewTextFile(string path, FileEventData fileEventData)
        {
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew);
                return new StreamWriter(stream);
            }
            catch (IOException)
            {
                if (File.Exists(path))
                {
                    throw new OptimisticConcurrencyException(
                        $"Event {fileEventData.AggregateSequenceNumber} already exists for entity with ID '{fileEventData.AggregateId}'");
                }

                throw;
            }
        }

        public async Task<IReadOnlyCollection<ICommittedDomainEvent>> LoadCommittedEventsAsync(
            IIdentity id,
            int fromEventSequenceNumber,
            CancellationToken cancellationToken)
        {
            using (await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                var committedDomainEvents = new List<ICommittedDomainEvent>();
                for (var i = fromEventSequenceNumber; ; i++)
                {
                    var eventPath = _filesEventLocator.GetEventPath(id, i);
                    if (!File.Exists(eventPath))
                    {
                        return committedDomainEvents;
                    }

                    var committedDomainEvent = await LoadFileEventDataFile(eventPath).ConfigureAwait(false);
                    committedDomainEvents.Add(committedDomainEvent);
                }
            }
        }

        public async Task DeleteEventsAsync(IIdentity id, CancellationToken cancellationToken)
        {
            _log.Verbose("Deleting entity with ID '{0}'", id);
            var path = _filesEventLocator.GetEntityPath(id);
            using (await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                Directory.Delete(path, true);
            }
        }

        private async Task<FileEventData> LoadFileEventDataFile(string eventPath)
        {
            using (var streamReader = File.OpenText(eventPath))
            {
                var json = await streamReader.ReadToEndAsync().ConfigureAwait(false);
                return _jsonSerializer.Deserialize<FileEventData>(json);
            }
        }

        private EventStoreLog RecreateEventStoreLog(string path)
        {
            var directory = Directory.GetDirectories(path)
                .SelectMany(Directory.GetDirectories)
                .SelectMany(Directory.GetFiles)
                .Select(f =>
                {
                    Console.WriteLine(f);
                    using (var streamReader = File.OpenText(f))
                    {
                        var json = streamReader.ReadToEnd();
                        var fileEventData = _jsonSerializer.Deserialize<FileEventData>(json);
                        return new { fileEventData.GlobalSequenceNumber, Path = f };
                    }
                })
                .ToDictionary(a => a.GlobalSequenceNumber, a => a.Path);

            return new EventStoreLog(
                directory.Keys.Any() ? directory.Keys.Max() : 0,
                directory);
        }
    }
}