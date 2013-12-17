﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using NMSD.Cronus.Core.Eventing;
using Cronus.Core.EventStore;
using NMSD.Cronus.Core.Cqrs;
using NMSD.Cronus.Core.Messaging;
using NMSD.Cronus.Core.Snapshotting;
using Protoreg;

namespace NMSD.Cronus.Core.EventStoreEngine
{
    [DataContract(Name = "987a7bed-7689-4c08-b610-9a802d306215")]
    public class Wraper
    {
        Wraper() { }

        public Wraper(List<object> events)
        {
            Events = events;
        }

        [DataMember(Order = 1)]
        public List<object> Events { get; private set; }

    }
    public class ProtoEventStore : ISnapShotter, IEventStore
    {
        const string LoadAggregateStateQueryTemplate = @"SELECT TOP 1 AggregateState FROM {0}Snapshots WHERE AggregateId=@aggregateId ORDER BY Version DESC";

        const string LoadEventsQueryTemplate = @"SELECT Events FROM {0}Events ORDER BY Revision OFFSET @offset ROWS FETCH NEXT {1} ROWS ONLY";

        private readonly string connectionString;

        ConcurrentDictionary<Type, Tuple<string, string>> eventsInfo = new ConcurrentDictionary<Type, Tuple<string, string>>();

        private readonly ProtoregSerializer serializer;

        ConcurrentDictionary<Type, Tuple<string, string>> snapshotsInfo = new ConcurrentDictionary<Type, Tuple<string, string>>();

        public ProtoEventStore(string connectionString, ProtoregSerializer serializer)
        {
            this.connectionString = connectionString;
            this.serializer = serializer;
        }

        public void CloseConnection(SqlConnection conn)
        {
            conn.Close();
        }

        public IEnumerable<IEvent> GetEventsFromStart(string boundedContext, int batchPerQuery = 1)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                string query = String.Format(LoadEventsQueryTemplate, boundedContext, batchPerQuery);
                SqlCommand command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@offset", 0);

                for (int i = 0; true; i++)
                {
                    command.Parameters[0].Value = i * batchPerQuery;
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.HasRows) break;

                        while (reader.Read())
                        {
                            var buffer = reader[0] as byte[];
                            Wraper wraper;
                            using (var stream = new MemoryStream(buffer))
                            {
                                wraper = (Wraper)serializer.Deserialize(stream);
                            }
                            foreach (IEvent @event in wraper.Events)
                            {
                                yield return @event;
                            }
                        }
                    }
                }
            }
        }

        public IAggregateRootState LoadAggregateState(string boundedContext, Guid aggregateId)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                string query = String.Format(LoadAggregateStateQueryTemplate, boundedContext);
                SqlCommand command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@aggregateId", aggregateId);

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var buffer = reader[0] as byte[];
                        IAggregateRootState state;
                        using (var stream = new MemoryStream(buffer))
                        {
                            state = (IAggregateRootState)serializer.Deserialize(stream);
                        }
                        return state;
                    }
                    else
                    {
                        return default(IAggregateRootState);
                    }
                }
            }
        }

        public SqlConnection OpenConnection()
        {
            var conn = new SqlConnection(connectionString);
            conn.Open();
            return conn;
        }

        public void Persist(List<IEvent> events, SqlConnection connection)
        {
            if (events == null) throw new ArgumentNullException("events");
            if (events.Count == 0) return;

            byte[] buffer = SerializeEvents(events);

            DataTable eventsTable = CreateInMemoryTableForEvents();
            var row = eventsTable.NewRow();
            row[1] = buffer;
            row[2] = events.Count;
            row[3] = DateTime.UtcNow;
            eventsTable.Rows.Add(row);

            using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
            {
                Tuple<string, string> eventInfo;
                Type firstEventType = events.First().GetType();
                if (!eventsInfo.TryGetValue(firstEventType, out eventInfo))
                {
                    string boundedContext = MessagingHelper.GetBoundedContext(firstEventType);
                    string table = String.Format("dbo.{0}Events", boundedContext);
                    eventInfo = new Tuple<string, string>(boundedContext, table);
                    eventsInfo.TryAdd(firstEventType, eventInfo);
                }

                bulkCopy.DestinationTableName = eventInfo.Item2;
                try
                {
                    bulkCopy.WriteToServer(eventsTable, DataRowState.Added);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
        }



        public void TakeSnapshot(List<IAggregateRootState> states, SqlConnection connection)
        {
            DataTable dt = CreateInMemoryTableForSnapshots();

            foreach (var state in states)
            {
                byte[] buffer = SerializeAggregateState(state);

                var row = dt.NewRow();
                row[0] = state.Version;
                row[1] = state.Id.Id;
                row[2] = buffer;
                row[3] = DateTime.UtcNow;
                dt.Rows.Add(row);
            }

            using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
            {
                Tuple<string, string> snapshotInfo;
                Type firstSnapshotType = states.First().GetType();
                if (!snapshotsInfo.TryGetValue(firstSnapshotType, out snapshotInfo))
                {
                    string boundedContext = MessagingHelper.GetBoundedContext(firstSnapshotType);
                    string table = String.Format("dbo.{0}Snapshots", boundedContext);
                    snapshotInfo = new Tuple<string, string>(boundedContext, table);
                    snapshotsInfo.TryAdd(firstSnapshotType, snapshotInfo);
                }

                bulkCopy.DestinationTableName = snapshotInfo.Item2;

                try
                {
                    bulkCopy.WriteToServer(dt, DataRowState.Added);
                }
                catch (Exception ex)
                {
                    throw new AggregateStateFirstLevelConcurrencyException("", ex);
                }
            }
        }

        private static DataTable CreateInMemoryTableForEvents()
        {
            DataTable uncommittedEvents = new DataTable();

            DataColumn revision = new DataColumn();
            revision.DataType = typeof(int);
            revision.ColumnName = "Revision";
            revision.AutoIncrement = true;
            revision.Unique = true;
            uncommittedEvents.Columns.Add(revision);

            DataColumn events = new DataColumn();
            events.DataType = typeof(byte[]);
            events.ColumnName = "Events";
            uncommittedEvents.Columns.Add(events);

            DataColumn eventsCount = new DataColumn();
            eventsCount.DataType = typeof(uint);
            eventsCount.ColumnName = "EventsCount";
            uncommittedEvents.Columns.Add(eventsCount);

            DataColumn timestamp = new DataColumn();
            timestamp.DataType = typeof(DateTime);
            timestamp.ColumnName = "Timestamp";
            uncommittedEvents.Columns.Add(timestamp);

            DataColumn[] keys = new DataColumn[1];
            keys[0] = revision;
            uncommittedEvents.PrimaryKey = keys;

            return uncommittedEvents;
        }

        private static DataTable CreateInMemoryTableForSnapshots()
        {
            DataTable uncommittedState = new DataTable();

            DataColumn version = new DataColumn();
            version.DataType = typeof(int);
            version.ColumnName = "Version";
            uncommittedState.Columns.Add(version);

            DataColumn aggregateId = new DataColumn();
            aggregateId.DataType = typeof(Guid);
            aggregateId.ColumnName = "AggregateId";
            uncommittedState.Columns.Add(aggregateId);

            DataColumn events = new DataColumn();
            events.DataType = typeof(byte[]);
            events.ColumnName = "AggregateState";
            uncommittedState.Columns.Add(events);

            DataColumn timestamp = new DataColumn();
            timestamp.DataType = typeof(DateTime);
            timestamp.ColumnName = "Timestamp";
            uncommittedState.Columns.Add(timestamp);

            DataColumn[] keys = new DataColumn[2];
            keys[0] = version;
            keys[1] = aggregateId;
            uncommittedState.PrimaryKey = keys;

            return uncommittedState;
        }

        private byte[] SerializeAggregateState(IAggregateRootState aggregateRootState)
        {
            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, aggregateRootState);
                return stream.ToArray();
            }
        }

        private byte[] SerializeEvents(List<IEvent> events)
        {
            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, new Wraper(events.Cast<object>().ToList()));
                return stream.ToArray();
            }
        }

    }

    public static class MeasureExecutionTime
    {
        public static string Start(System.Action action)
        {
            string result = string.Empty;

            var stopWatch = new System.Diagnostics.Stopwatch();
            stopWatch.Start();
            action();
            stopWatch.Stop();
            System.TimeSpan ts = stopWatch.Elapsed;
            result = System.String.Format("{0:00}:{1:00}:{2:00}.{3:00}", ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds / 10);
            return result;
        }

        public static string Start(System.Action action, int repeat, bool showTicksInfo = false)
        {
            var stopWatch = new System.Diagnostics.Stopwatch();
            stopWatch.Start();
            for (int i = 0; i < repeat; i++)
            {
                action();
            }
            stopWatch.Stop();
            System.TimeSpan total = stopWatch.Elapsed;
            System.TimeSpan average = new System.TimeSpan(stopWatch.Elapsed.Ticks / repeat);

            System.Text.StringBuilder perfResultsBuilder = new System.Text.StringBuilder();
            perfResultsBuilder.AppendLine("--------------------------------------------------------------");
            perfResultsBuilder.AppendFormat("  Total Time => {0}\r\nAverage Time => {1}", Align(total), Align(average));
            perfResultsBuilder.AppendLine();
            perfResultsBuilder.AppendLine("--------------------------------------------------------------");
            if (showTicksInfo)
                perfResultsBuilder.AppendLine(TicksInfo());
            return perfResultsBuilder.ToString();
        }

        static string Align(System.TimeSpan interval)
        {
            string intervalStr = interval.ToString();
            int pointIndex = intervalStr.IndexOf(':');

            pointIndex = intervalStr.IndexOf('.', pointIndex);
            if (pointIndex < 0) intervalStr += "        ";
            return intervalStr;
        }

        static string TicksInfo()
        {
            System.Text.StringBuilder ticksInfoBuilder = new System.Text.StringBuilder("\r\n\r\n");
            ticksInfoBuilder.AppendLine("Ticks Info");
            ticksInfoBuilder.AppendLine("--------------------------------------------------------------");
            const string numberFmt = "{0,-22}{1,18:N0}";
            const string timeFmt = "{0,-22}{1,26}";

            ticksInfoBuilder.AppendLine(System.String.Format(numberFmt, "Field", "Value"));
            ticksInfoBuilder.AppendLine(System.String.Format(numberFmt, "-----", "-----"));

            // Display the maximum, minimum, and zero TimeSpan values.
            ticksInfoBuilder.AppendLine(System.String.Format(timeFmt, "Maximum TimeSpan", Align(System.TimeSpan.MaxValue)));
            ticksInfoBuilder.AppendLine(System.String.Format(timeFmt, "Minimum TimeSpan", Align(System.TimeSpan.MinValue)));
            ticksInfoBuilder.AppendLine(System.String.Format(timeFmt, "Zero TimeSpan", Align(System.TimeSpan.Zero)));
            ticksInfoBuilder.AppendLine();

            // Display the ticks-per-time-unit fields.
            ticksInfoBuilder.AppendLine(System.String.Format(numberFmt, "Ticks per day", System.TimeSpan.TicksPerDay));
            ticksInfoBuilder.AppendLine(System.String.Format(numberFmt, "Ticks per hour", System.TimeSpan.TicksPerHour));
            ticksInfoBuilder.AppendLine(System.String.Format(numberFmt, "Ticks per minute", System.TimeSpan.TicksPerMinute));
            ticksInfoBuilder.AppendLine(System.String.Format(numberFmt, "Ticks per second", System.TimeSpan.TicksPerSecond));
            ticksInfoBuilder.AppendLine(System.String.Format(numberFmt, "Ticks per millisecond", System.TimeSpan.TicksPerMillisecond));
            ticksInfoBuilder.AppendLine("--------------------------------------------------------------");
            return ticksInfoBuilder.ToString();
        }
    }
}
