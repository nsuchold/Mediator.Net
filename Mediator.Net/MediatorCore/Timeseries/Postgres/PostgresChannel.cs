﻿// Licensed to ifak e.V. under one or more agreements.
// ifak e.V. licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using NLog;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using NpgsqlTypes;

namespace Ifak.Fast.Mediator.Timeseries.Postgres
{
    public class PostgresChannel : Channel
    {
        private static Logger logger = LogManager.GetLogger("PostgresChannel");

        private readonly DbConnection connection;
        private readonly ChannelInfo info;
        private readonly string table;

        private readonly PreparedStatement stmtUpdate;
        private readonly PreparedStatement stmtInsert;
        private readonly PreparedStatement stmtUpsert;
        private readonly PreparedStatement stmtDelete;
        private readonly PreparedStatement stmtDeleteOne;
        private readonly PreparedStatement stmtLast;
        private readonly PreparedStatement stmtLatestTimeDb;
        private readonly PreparedStatement stmtRawFirstN;
        private readonly PreparedStatement stmtRawLastN;
        private readonly PreparedStatement stmtRawFirst;
        private readonly PreparedStatement stmtCount;
        private readonly PreparedStatement stmtCountInterval;

        public PostgresChannel(DbConnection connection, ChannelInfo info, string tableName) {
            this.connection = connection;
            this.info = info;
            this.table = "\"" + tableName + "\"";

            NpgsqlDbType time = NpgsqlDbType.Timestamp;
            NpgsqlDbType diffDB = NpgsqlDbType.Integer;
            NpgsqlDbType quality = NpgsqlDbType.Smallint;
            NpgsqlDbType data = NpgsqlDbType.Text;

            stmtUpdate        = new PreparedStatement(connection, $"UPDATE {table} SET diffDB = @1, quality = @2, data = @3 WHERE time = @4", 4, diffDB, quality, data, time);
            stmtInsert        = new PreparedStatement(connection, $"INSERT INTO {table} VALUES (@1, @2, @3, @4)", 4, time, diffDB, quality, data);
            stmtUpsert        = new PreparedStatement(connection, $"INSERT INTO {table} VALUES (@1, @2, @3, @4) ON CONFLICT (time) DO UPDATE SET diffDB = @2, quality = @3, data = @4", 4, time, diffDB, quality, data);
            stmtDelete        = new PreparedStatement(connection, $"DELETE FROM {table} WHERE time BETWEEN @1 AND @2", 2, time, time);
            stmtDeleteOne     = new PreparedStatement(connection, $"DELETE FROM {table} WHERE time = @1", 1, time);
            stmtLast          = new PreparedStatement(connection, $"SELECT * FROM {table} ORDER BY time DESC LIMIT 1", 0);
            stmtLatestTimeDb  = new PreparedStatement(connection, $"SELECT * FROM {table} WHERE time BETWEEN @1 AND @2 ORDER BY (time + 1000 * diffDB) DESC LIMIT 1", 2, time, time);
            stmtRawFirstN     = new PreparedStatement(connection, $"SELECT * FROM {table} WHERE time BETWEEN @1 AND @2 ORDER BY time ASC LIMIT @3", 3, time, time, NpgsqlDbType.Integer);
            stmtRawLastN      = new PreparedStatement(connection, $"SELECT * FROM {table} WHERE time BETWEEN @1 AND @2 ORDER BY time DESC LIMIT @3", 3, time, time, NpgsqlDbType.Integer);
            stmtRawFirst      = new PreparedStatement(connection, $"SELECT * FROM {table} WHERE time BETWEEN @1 AND @2 ORDER BY time ASC", 2, time, time);
            stmtCount         = new PreparedStatement(connection, $"SELECT COUNT(*) FROM {table}", 0);
            stmtCountInterval = new PreparedStatement(connection, $"SELECT COUNT(*) FROM {table} WHERE time BETWEEN @1 AND @2", 2, time, time);
        }

        public override ChannelInfo Info => info;

        public override long CountAll() {
            return (long)stmtCount.ExecuteScalar();
        }

        public override long CountData(Timestamp startInclusive, Timestamp endInclusive) {
            stmtCountInterval[0] = startInclusive.ToDateTime();
            stmtCountInterval[1] = endInclusive.ToDateTime();
            return (long)stmtCountInterval.ExecuteScalar();
        }

        public override long DeleteAll() {
            using (var command = Factory.MakeCommand($"DELETE FROM {table}", connection)) {
                return command.ExecuteNonQuery();
            }
        }

        public override long DeleteData(Timestamp[] timestamps) {
            return stmtDeleteOne.RunTransaction(stmt => {
                long counter = 0;
                for (int i = 0; i < timestamps.Length; ++i) {
                    stmt[0] = timestamps[i].ToDateTime();
                    counter += stmt.ExecuteNonQuery();
                }
                return counter;
            });
        }

        public override long DeleteData(Timestamp startInclusive, Timestamp endInclusive) {
            return stmtDelete.RunTransaction(stmt => {
                stmt[0] = startInclusive.ToDateTime();
                stmt[1] = endInclusive.ToDateTime();
                return stmt.ExecuteNonQuery();
            });
        }

        public override VTTQ? GetLatest() => GetLatest(null);

        protected VTTQ? GetLatest(DbTransaction transaction) {

            using (var reader = stmtLast.ExecuteReader(transaction)) {
                if (reader.Read()) {
                    return ReadVTTQ(reader);
                }
                else {
                    return null;
                }
            }
        }

        public override VTTQ? GetLatestTimestampDB(Timestamp startInclusive, Timestamp endInclusive) {
            stmtLatestTimeDb[0] = startInclusive.ToDateTime();
            stmtLatestTimeDb[1] = endInclusive.ToDateTime();
            using (var reader = stmtLatestTimeDb.ExecuteReader()) {
                if (reader.Read()) {
                    return ReadVTTQ(reader);
                }
                else {
                    return null;
                }
            }
        }

        public override void Insert(VTQ[] data) {

            if (data.Length == 0) return;

            Timestamp timeDB = Timestamp.Now;

            stmtInsert.RunTransaction(stmt => {

                for (int i = 0; i < data.Length; ++i) {
                    VTQ x = data[i];
                    WriteVTQ(stmt, x, timeDB);
                    stmt.ExecuteNonQuery();
                }
                return true;
            });
        }

        public override void Upsert(VTQ[] data) {

            if (data.Length == 0) return;

            Timestamp timeDB = Timestamp.Now;

            stmtUpsert.RunTransaction(stmt => {

                for (int i = 0; i < data.Length; ++i) {
                    VTQ x = data[i];
                    WriteVTQ(stmt, x, timeDB);
                    stmt.ExecuteNonQuery();
                }
                return true;
            });
        }

        public override void Append(VTQ[] data, bool ignoreOldDataSets = false) {

            if (data.Length == 0) return;

            CheckIncreasingTimestamps(data);

            VTTQ? lastItem = GetLatest();

            if (lastItem.HasValue && data[0].T <= lastItem.Value.T) {

                if (ignoreOldDataSets) {
                    Timestamp t = lastItem.Value.T;
                    VTQ[] filtered = data.Where(x => x.T > t).ToArray();
                    Insert(filtered);
                }
                else {
                    throw new Exception("Timestamp is smaller or equal than last dataset timestamp in channel DB!\n\tLastItem in Database: " + lastItem.Value.ToString() + "\n\tFirstItem to Append:  " + data[0].ToString());
                }
            }
            else {
                Insert(data);
            }
        }

        private static void CheckIncreasingTimestamps(VTQ[] data) {
            Timestamp tPrev = Timestamp.Empty;
            for (int i = 0; i < data.Length; ++i) {
                Timestamp t = data[i].T;
                if (t <= tPrev) throw new Exception("Dataset timestamps are not monotonically increasing!");
                tPrev = t;
            }
        }

        public override void Update(VTQ[] data) {

            Timestamp timeDB = Timestamp.Now;

            stmtUpdate.RunTransaction(stmt => {

                for (int i = 0; i < data.Length; ++i) {
                    VTQ x = data[i];
                    stmt[0] = (timeDB - x.T).TotalMilliseconds / 1000L;
                    stmt[1] = (int)x.Q;
                    stmt[2] = x.V.JSON;
                    stmt[3] = x.T.ToDateTime();
                    int updatedRows = stmt.ExecuteNonQuery();
                    if (updatedRows != 1) {
                        throw new Exception("Update of missing timestamp '" + x.T + "' would fail.");
                    }
                }
                return true;
            });
        }

        public override Func<PrepareContext, string> PrepareAppend(VTQ data) {

            return (PrepareContext ctx) => {

                var context = (PostgresContext)ctx;

                VTTQ? lastItem = GetLatest(context.Transaction);

                if (lastItem.HasValue && data.T <= lastItem.Value.T) {
                    return table + ": Timestamp is smaller or equal than last dataset timestamp in channel DB!\n\tLastItem in Database: " + lastItem.Value.ToString() + "\n\t  The Item to Append: " + data.ToString();
                }

                PreparedStatement stmt = stmtInsert;
                try {
                    WriteVTQ(stmt, data, context.TimeDB);
                    stmt.ExecuteNonQuery(context.Transaction);
                    return null;
                }
                catch (Exception exp) {
                    stmt.Reset();
                    return table + ": " + exp.Message;
                }
            };
        }

        public override IList<VTTQ> ReadData(Timestamp startInclusive, Timestamp endInclusive, int maxValues, BoundingMethod bounding) {

            long N = CountData(startInclusive, endInclusive);

            PreparedStatement statement = null;

            switch (bounding) {
                case BoundingMethod.TakeFirstN:
                    statement = stmtRawFirstN;
                    break;

                case BoundingMethod.TakeLastN:
                    statement = stmtRawLastN;
                    break;

                case BoundingMethod.CompressToN:
                    if (N <= maxValues)
                        return ReadData(startInclusive, endInclusive, maxValues, BoundingMethod.TakeFirstN);
                    else
                        return ReadDataCompressed(startInclusive, endInclusive, maxValues, N);
            }

            statement[0] = startInclusive.ToDateTime();
            statement[1] = endInclusive.ToDateTime();
            statement[2] = maxValues;

            int initSize = N < maxValues ? (int)N : maxValues;
            var res = new List<VTTQ>(initSize);
            using (var reader = statement.ExecuteReader()) {
                while (reader.Read()) {
                    res.Add(ReadVTTQ(reader));
                }
            }

            if (bounding == BoundingMethod.TakeLastN) {
                res.Reverse();
            }

            return res;
        }

        private IList<VTTQ> ReadDataCompressed(Timestamp startInclusive, Timestamp endInclusive, int maxValues, long count) {

            PreparedStatement statement = stmtRawFirst;
            statement[0] = startInclusive.ToDateTime();
            statement[1] = endInclusive.ToDateTime();

            var result = new List<VTTQ>(maxValues);

            int maxIntervals = maxValues / 3;
            int itemsPerInterval = (maxValues < 6) ? (int)count : (int)Math.Ceiling(((double)count) / maxIntervals);
            var buffer = new List<VTTQ_D>(itemsPerInterval);

            using (var reader = statement.ExecuteReader()) {
                while (reader.Read()) {
                    VTTQ x = ReadVTTQ(reader);
                    if (!x.V.IsEmpty) {
                        buffer.Add(new VTTQ_D(x, x.V.AsDouble()));
                    }
                    if (buffer.Count >= itemsPerInterval) {
                        FlushBuffer(result, buffer, maxValues);
                    }
                }
            }

            if (buffer.Count > 0) {
                FlushBuffer(result, buffer, maxValues);
            }

            return result;
        }

        struct VTTQ_D
        {
            internal VTTQ V;
            internal double D;

            internal VTTQ_D(VTTQ x, double d) {
                V = x;
                D = d;
            }
        }

        private void FlushBuffer(List<VTTQ> result, List<VTTQ_D> buffer, int maxValues) {
            int N = buffer.Count;
            if (N > 3) {
                buffer.Sort(CompareVTTQs);
                if (maxValues >= 3) {
                    VTTQ a = buffer[0].V;
                    VTTQ b = buffer[N / 2].V;
                    VTTQ c = buffer[N - 1].V;
                    AddByTime(result, a, b, c);
                }
                else {
                    result.Add(buffer[N / 2].V);
                }
            }
            else {
                result.AddRange(buffer.Select(y => y.V));
            }
            buffer.Clear();
        }

        private static int CompareVTTQs(VTTQ_D a, VTTQ_D b) => a.D.CompareTo(b.D);

        private static void AddByTime(List<VTTQ> result, VTTQ a, VTTQ b, VTTQ c) {
            if (a.T < b.T && a.T < c.T) {
                result.Add(a);
                if (b.T < c.T) {
                    result.Add(b);
                    result.Add(c);
                }
                else {
                    result.Add(c);
                    result.Add(b);
                }
            }
            else if (b.T < a.T && b.T < c.T) {
                result.Add(b);
                if (a.T < c.T) {
                    result.Add(a);
                    result.Add(c);
                }
                else {
                    result.Add(c);
                    result.Add(a);
                }
            }
            else {
                result.Add(c);
                if (a.T < b.T) {
                    result.Add(a);
                    result.Add(b);
                }
                else {
                    result.Add(b);
                    result.Add(a);
                }
            }
        }

        private VTTQ ReadVTTQ(DbDataReader reader) {
            Timestamp t = Timestamp.FromDateTime(DateTime.SpecifyKind((DateTime)reader["time"], DateTimeKind.Utc));
            Timestamp tDB = t + Duration.FromSeconds((int)reader["diffDB"]);
            Quality q = (Quality)(short)reader["quality"];
            string data = (string)reader["data"];
            return new VTTQ(t, tDB, q, DataValue.FromJSON(data));
        }

        private void WriteVTQ(PreparedStatement stmt, VTQ data, Timestamp timeDB) {
            stmt[0] = data.T.ToDateTime();
            stmt[1] = (int)((timeDB - data.T).TotalMilliseconds / 1000L);
            stmt[2] = (short)(int)data.Q;
            stmt[3] = data.V.JSON;
        }
        // TODO Close or Dispose ?
    }

    class PreparedStatement
    {
        private readonly DbConnection connection;
        private readonly string sql;
        private readonly int countParameters;

        private DbCommand command = null;
        private static readonly string[] indices = new string[] { "1", "2", "3", "4"};
        private NpgsqlDbType[] types;

        internal PreparedStatement(DbConnection connection, string sql, int countParameters, params NpgsqlDbType[] types) {
            this.connection = connection;
            this.sql = sql;
            this.countParameters = countParameters;
            this.types = types;
        }

        DbCommand GetCommand() {
            if (command != null) {
                return command;
            }
            var cmd = (Npgsql.NpgsqlCommand)Factory.MakeCommand(sql, connection);
            for (int i = 0; i < countParameters; ++i) {
                cmd.Parameters.Add(indices[i], types[i]);
            }
            cmd.Prepare();

            command = cmd;
            return command;
        }

        internal object this[int i]
        {
            set
            {
                var cmd = GetCommand();
                cmd.Parameters[i].Value = value;
            }
        }

        internal int ExecuteNonQuery(DbTransaction transaction = null) {
            var command = GetCommand();
            if (transaction != null) {
                command.Transaction = transaction;
            }
            return command.ExecuteNonQuery();
        }

        internal object ExecuteScalar(DbTransaction transaction = null) {
            var command = GetCommand();
            if (transaction != null) {
                command.Transaction = transaction;
            }
            return command.ExecuteScalar();
        }

        internal DbDataReader ExecuteReader(DbTransaction transaction = null) {
            var command = GetCommand();
            if (transaction != null) {
                command.Transaction = transaction;
            }
            return command.ExecuteReader();
        }

        internal T RunTransaction<T>(Func<PreparedStatement, T> operation) {

            var cmd = GetCommand();

            using (var transaction = connection.BeginTransaction()) {
                try {
                    cmd.Transaction = transaction;
                    T res = operation(this);
                    transaction.Commit();
                    cmd.Transaction = null;
                    return res;
                }
                catch (Exception) {

                    try {
                        transaction.Rollback();
                    }
                    catch (Exception) { }

                    Reset();
                    //logger.Error(ex, "Creating channels failed: " + ex.Message);
                    throw;
                }
            }
        }

        internal void Reset() {
            if (command != null) {
                command.Transaction = null;
                command.Dispose();
                command = null;
            }
        }
    }
}
