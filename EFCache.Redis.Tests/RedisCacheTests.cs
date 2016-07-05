﻿using System;
using System.Threading;
using EFCache.Redis.Tests.Annotations;
using StackExchange.Redis;
using Xunit;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;

namespace EFCache.Redis.Tests
{
    [Serializable]
    public class TestObject
    {
        public string Message { get; set; }
    }

    [UsedImplicitly]
    public class RedisCacheTests
    {
        public RedisCacheTests()
        {
            RedisStorageEmulatorManager.Instance.StartProcess(false);
        }
        [Fact]
        public void Item_cached()
        {
            var cache = new RedisCache("localhost:6379");
            var item = new TestObject { Message = "OK" };

            cache.PutItem("key", item, new string[0], TimeSpan.MaxValue, DateTimeOffset.MaxValue);

            object fromCache;

            Assert.True(cache.GetItem("key", out fromCache));
            Assert.Equal(item.Message, ((TestObject)fromCache).Message);

            Assert.True(cache.GetItem("key", out fromCache));
            Assert.Equal(item.Message, ((TestObject)fromCache).Message);
        }

        [Fact]
        public void Item_not_returned_after_absolute_expiration_expired()
        {
            var cache = new RedisCache("localhost:6379");
            var item = new TestObject { Message = "OK" };

            cache.PutItem("key", item, new string[0], TimeSpan.MaxValue, DateTimeOffset.Now.AddMinutes(-10));

            object fromCache;
            Assert.False(cache.GetItem("key", out fromCache));
            Assert.Null(fromCache);
        }

        [Fact]
        public void Item_not_returned_after_sliding_expiration_expired()
        {
            var cache = new RedisCache("localhost:6379");
            var item = new TestObject { Message = "OK" };

            cache.PutItem("key", item, new string[0], TimeSpan.Zero.Subtract(new TimeSpan(10000)), DateTimeOffset.MaxValue);

            object fromCache;
            Assert.False(cache.GetItem("key", out fromCache));
            Assert.Null(fromCache);
        }

        [Fact]
        public void Item_still_returned_after_sliding_expiration_period()
        {
            var cache = new RedisCache("localhost:6379");
            var item = new TestObject { Message = "OK" };

            // Cache the item with a sliding expiration of 10 seconds
            cache.PutItem("key", item, new string[0], TimeSpan.FromSeconds(10), DateTimeOffset.MaxValue);

            object fromCache = null;
            // In a loop of 20 seconds retrieve the item every 5 second seconds.
            for (var i = 0; i < 4; i++)
            {
                Thread.Sleep(5000); // Wait 5 seconds
                // Retrieve item again. This should update LastAccess and as such keep the item 'alive'
                // Break when item cannot be retrieved
                Assert.True(cache.GetItem("key", out fromCache));
            }
            Assert.NotNull(fromCache);
        }

        [Fact]
        public void InvalidateSets_invalidate_items_with_given_sets()
        {
            var cache = new RedisCache("localhost:6379");

            cache.PutItem("1", new object(), new[] { "ES1", "ES2" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);
            cache.PutItem("2", new object(), new[] { "ES2", "ES3" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);
            cache.PutItem("3", new object(), new[] { "ES1", "ES3", "ES4" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);
            cache.PutItem("4", new object(), new[] { "ES3", "ES4" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);

            cache.InvalidateSets(new[] { "ES1", "ES2" });

            object item;
            Assert.False(cache.GetItem("1", out item));
            Assert.False(cache.GetItem("2", out item));
            Assert.False(cache.GetItem("3", out item));
            Assert.True(cache.GetItem("4", out item));
        }

        [Fact]
        public void InvalidateItem_invalidates_item()
        {
            var cache = new RedisCache("localhost:6379");

            cache.PutItem("1", new object(), new[] { "ES1", "ES2" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);
            cache.InvalidateItem("1");

            object item;
            Assert.False(cache.GetItem("1", out item));
        }

        [Fact]
        public void Count_returns_numers_of_cached_entries()
        {
            var cache = new RedisCache("localhost:6379,allowAdmin=true");

            cache.Purge();

            Assert.Equal(0, cache.Count);

            cache.PutItem("1", new object(), new[] { "ES1", "ES2" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);

            Assert.Equal(3, cache.Count); // "1", "ES1", "ES2"

            cache.InvalidateItem("1");

            Assert.Equal(0, cache.Count);
        }

        [Fact]
        public async Task ThreadingBlockTest()
        {
            var cache = new RedisCache("localhost:6379,allowAdmin=true");

            Exception exception = null;

            cache.LockWaitTimeout = 250;

            cache.CachingFailed += (sender, e) =>
            {
                if (e?.InnerException is LockTimeoutException)
                    exception = e.InnerException;
            };
            cache.Purge();

            Assert.Equal(0, cache.Count);

            var crazyLargeResultSet = Enumerable.Range(1, 100000).Select(a => $"String {a}").ToArray();

            cache.PutItem("1", crazyLargeResultSet, new[] { "ES1", "ES2" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);
            cache.PutItem("2", crazyLargeResultSet, new[] { "ES1", "ES2" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);
            cache.PutItem("3", crazyLargeResultSet, new[] { "ES1", "ES2" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);

            //            Assert.Equal(3, cache.Count); // "1", "ES1", "ES2"


            var tasks = new Task[10];

            for (var i = 0; i < 10; i++)
            {
                var icopy = i;
                tasks[i] = Task.Run(() =>
                {
                    var watch = new Stopwatch();
                    watch.Start();
                    Debug.WriteLine($"Invalidate {icopy} start");
                    if (i == 9)
                    cache.InvalidateItem("1");
                    else
                    {
                        object val;
                        cache.GetItem("1", out val);
                    }
                    watch.Stop();
                    Debug.WriteLine($"Invalidate {icopy} complete after {watch.ElapsedMilliseconds}");
                });
            }


            var threadGet = Task.Run(() =>
            {
                Debug.WriteLine($"Get start");
                var watch = new Stopwatch();
                watch.Start();
                object value;
                cache.GetItem("1", out value);
                watch.Stop();
                Debug.WriteLine($"Get complete after {watch.ElapsedMilliseconds}");
            });


            await threadGet;
            await Task.WhenAll(tasks);

            Assert.NotNull(exception);
            Assert.IsType<LockTimeoutException>(exception);


        }

        private void Cache_CachingFailed(object sender, RedisCacheException e)
        {
            throw new NotImplementedException();
        }

        [Fact]
        public void Purge_removes_stale_items_from_cache()
        {
            var cache = new RedisCache("localhost:6379,allowAdmin=true");

            cache.Purge();

            cache.PutItem("1", new object(), new[] { "ES1", "ES2" }, TimeSpan.MaxValue, DateTimeOffset.Now.AddMinutes(-1));
            cache.PutItem("2", new object(), new[] { "ES1", "ES2" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);

            Assert.Equal(4, cache.Count); // "1", "2", "ES1", "ES2"

            cache.Purge();

            Assert.Equal(0, cache.Count);

            object item;
            Assert.False(cache.GetItem("1", out item));
            Assert.False(cache.GetItem("2", out item));
        }

        [Fact]
        public void GetItem_validates_parameters()
        {
            object item;

            Assert.Equal(
                "key",
                Assert.Throws<ArgumentOutOfRangeException>(() => new RedisCache("localhost:6379").GetItem(null, out item)).ParamName);
        }

        [Fact]
        public void PutItem_validates_parameters()
        {
            Assert.Equal(
                "key",
                Assert.Throws<ArgumentOutOfRangeException>(()
                    => new RedisCache("localhost:6379").PutItem(null, 42, new string[0], TimeSpan.Zero, DateTimeOffset.Now))
                    .ParamName);

            Assert.Equal(
                "dependentEntitySets",
                Assert.Throws<ArgumentNullException>(()
                    => new RedisCache("localhost:6379").PutItem("1", 42, null, TimeSpan.Zero, DateTimeOffset.Now)).ParamName);
        }

        [Fact]
        public void InvalidateSets_validates_parameters()
        {
            Assert.Equal(
                "entitySets",
                Assert.Throws<ArgumentNullException>(() => new RedisCache("localhost:6379").InvalidateSets(null)).ParamName);
        }

        [Fact]
        public void InvalidateItem_validates_parameters()
        {
            Assert.Equal(
                "key",
                Assert.Throws<ArgumentOutOfRangeException>(() => new RedisCache("localhost:6379").InvalidateItem(null)).ParamName);
        }

        [Fact]
        public void GetItem_does_not_crash_if_cache_is_unavailable()
        {
            var cache = new RedisCache("unknown,abortConnect=false");
            RedisCacheException exception = null;
            cache.CachingFailed += (s, e) => exception = e;

            object item;
            var success = cache.GetItem("1", out item);

            Assert.False(success);
            Assert.Null(item);
            Assert.NotNull(exception);
            Assert.IsType(typeof(RedisConnectionException), exception.InnerException);
            Assert.Equal(exception.Message, "Caching failed for GetItem");
        }

        [Fact]
        public void PutItem_does_not_crash_if_cache_is_unavailable()
        {
            var cache = new RedisCache("unknown,abortConnect=false");
            RedisCacheException exception = null;
            cache.CachingFailed += (s, e) => exception = e;

            cache.PutItem("1", new object(), new[] { "ES1", "ES2" }, TimeSpan.MaxValue, DateTimeOffset.MaxValue);

            Assert.NotNull(exception);
            Assert.IsType(typeof(RedisConnectionException), exception.InnerException);
        }

        [Fact]
        public void InvalidateItem_does_not_crash_if_cache_is_unavailable()
        {
            var cache = new RedisCache("unknown,abortConnect=false");
            RedisCacheException exception = null;
            cache.CachingFailed += (s, e) => exception = e;

            cache.InvalidateItem("1");

            Assert.NotNull(exception);
            Assert.IsType(typeof(RedisConnectionException), exception.InnerException);
        }
    }
}
