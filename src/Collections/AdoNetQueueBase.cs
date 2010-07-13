﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using DatabaseQueue.Data;
using DatabaseQueue.Extensions;
using DatabaseQueue.Serialization;

namespace DatabaseQueue.Collections
{
    /// <summary>
    /// An ADO.NET base class implementation of IDatabaseQueue[T]
    /// </summary>
    public abstract class AdoNetQueueBase<T> : IDatabaseQueue<T>
    {
        private readonly ISerializer<T> _serializer;

        private int _disposed;
        private int _count;

        protected AdoNetQueueBase(IStorageSchema schema, ISerializer<T> serializer)
        {
            Schema = schema;
            _serializer = serializer;
        }

        /// <summary>
        /// If true, a seperate round trip to the database using GetTableExistsCommandText/0 
        /// is made before deciding to call GetCreateTableCommandText based on the result.
        /// Default: false
        /// </summary>
        protected bool CheckTableExists { get; set; }

        protected IDbConnection Connection { get; private set; }

        protected IStorageSchema Schema { get; private set; }

        private void EnsureConnectionIsOpen()
        {
            if (Connection == null)
                throw new NullReferenceException("Ensure a call to Initialize/0 is made before using the queue");

            switch (Connection.State)
            {
                case ConnectionState.Closed:
                case ConnectionState.Broken:
                    Connection.Open();
                    break;
            }
        }

        private void EnsureTableExists()
        {
            if (CheckTableExists)
            {
                using (var exists = CreateCommand(Schema.TableExistsCommandText))
                {
                    if ((int)exists.ExecuteScalar() > 0)
                        return;
                }
            }

            var createText = Schema.CreateTableCommandText;

            using (var create = CreateCommand(createText))
                create.ExecuteNonQuery();
        }

        #region Abstract / Virtual Members

        protected abstract IDbConnection CreateConnection();

        #endregion

        #region Command Creation

        private IDbCommand CreateInsertCommand(out IDbDataParameter valueParameter)
        {
            var commandText = Schema.InsertCommandText;
            var command = CreateCommand(commandText);

            valueParameter = command.CreateParameter();
            valueParameter.DbType = Schema.Value.ParameterType;
            command.Parameters.Add(valueParameter);

            return command;
        }

        private IDbCommand CreateSelectCommand(int max)
        {
            var commandText = Schema.GetSelectCommandText(max);

            return CreateCommand(commandText);
        }

        private IDbCommand CreateDeleteCommand(out IDbDataParameter keyParameter)
        {
            var commandText = Schema.DeleteCommandText;
            var command = CreateCommand(commandText);

            keyParameter = command.CreateParameter();
            keyParameter.DbType = Schema.Key.ParameterType;
            command.Parameters.Add(keyParameter);

            return command;
        }

        private IDbCommand CreateCountCommand()
        {
            var commandText = Schema.CountCommandText;

            return CreateCommand(commandText);
        }

        private IDbCommand CreateCommand(string commandText)
        {
            var command = Connection.CreateCommand();
            command.CommandText = commandText;

            return command;
        }

        #endregion

        #region Bulk Insert / Select / Delete

        private int ExecuteCountCommand()
        {
            using (var command = CreateCountCommand())
                return Convert.ToInt32(command.ExecuteScalar());
        }

        private int ExecuteInsertCommand(IEnumerable<T> items)
        {
            var rows = 0;
            IDbDataParameter insertParameter;

            using (var command = CreateInsertCommand(out insertParameter))
            {
                foreach (var item in items)
                {
                    object serialized;

                    if (_serializer.TrySerialize(item, out serialized))
                        insertParameter.Value = serialized;

                    if (command.ExecuteNonQuery() != 1)
                        continue;

                    rows++;

                    Interlocked.Increment(ref _count);
                }
            }

            return rows;
        }

        private void ExecuteSelectAndDeleteCommand(int max, ICollection<T> items)
        {
            using (var select = CreateSelectCommand(max))
            {
                IDbDataParameter deleteParameter;

                using (var delete = CreateDeleteCommand(out deleteParameter))
                {
                    using (var reader = select.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var value = reader.GetValue(Schema.Value);

                            deleteParameter.Value = reader.GetValue(Schema.Key);
                            T item;

                            if (!_serializer.TryDeserialize(value, out item)
                                || delete.ExecuteNonQuery() != 1)
                            {
                                continue;
                            }

                            items.Add(item);

                            Interlocked.Decrement(ref _count);
                        }
                    }
                }
            }
        }

        private bool TryInsertMultiple(ICollection<T> items)
        {
            if (items.IsNullOrEmpty())
                return false;

            EnsureConnectionIsOpen();

            var rows = 0;

            using (var transaction = Connection.BeginTransaction())
            {
                try
                {
                    rows = ExecuteInsertCommand(items);

                    transaction.Commit();
                }
                catch (InvalidOperationException)
                {
                    transaction.Rollback();
                }
            }

            return rows == items.Count;
        }

        private bool TrySelectAndDeleteMultiple(out ICollection<T> items, int max)
        {
            var success = false;
            items = new List<T>();

            if (max < 1)
                return false;

            EnsureConnectionIsOpen();

            using (var transaction = Connection.BeginTransaction())
            {
                try
                {
                    ExecuteSelectAndDeleteCommand(max, items);

                    transaction.Commit();

                    success = true;
                }
                catch (Exception ex)
                {
                    transaction.Rollback();

                    success = false;
                }
            }

            return success && items.Count > 0;
        }

        #endregion

        #region IDatabaseQueue<T> Members

        public virtual void Initialize()
        {
            Connection = CreateConnection();

            EnsureConnectionIsOpen();
            EnsureTableExists();

            _count = ExecuteCountCommand();
        }

        #endregion

        #region IQueue<T> Members

        public int Count { get { return _count; } }

        public bool TryEnqueueMultiple(ICollection<T> items)
        {
            return TryInsertMultiple(items);
        }

        public bool TryDequeueMultiple(out ICollection<T> items, int max)
        {
            return TrySelectAndDeleteMultiple(out items, max);
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (disposing)
            {
                // Dispose managed resources
                Connection.Close();
            }

            // Dispose unmanaged resources
        }

        ~AdoNetQueueBase()
        {
            Dispose(false);
        }

        #endregion
    }
}
