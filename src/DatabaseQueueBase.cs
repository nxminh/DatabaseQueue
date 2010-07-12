﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;

namespace DatabaseQueue
{
    public abstract class DatabaseQueueBase<T> : IDatabaseQueue<T>
    {
        private readonly ISerializer<T> _serializer;

        private int _disposed;
        private int _count;

        protected DatabaseQueueBase(IStorageSchema schema, ISerializer<T> serializer)
        {
            Schema = schema;
            _serializer = serializer;
        }
        
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

        #region Abstract / Virtual Members

        protected abstract IDbConnection CreateConnection();

        protected abstract IDbCommand CreateDeleteCommand(out IDbDataParameter keyParameter);
        protected abstract IDbCommand CreateInsertCommand(out IDbDataParameter valueParameter);
        protected abstract IDbCommand CreateSelectCommand(int max);
        protected abstract IDbCommand CreateCountCommand();

        protected abstract void EnsureTableExists();

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
                            object key = reader.GetValue(Schema.Key),
                                value = reader.GetValue(Schema.Value);

                            T item;

                            if (!_serializer.TryDeserialize(value, out item))
                                continue;

                            deleteParameter.Value = key;
                            
                            if (delete.ExecuteNonQuery() == 1)
                               items.Add(item);
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

        ~DatabaseQueueBase()
        {
            Dispose(false);
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
    }
}
