using System.Text;
using System.Numerics;
using Phantasma.Core.Utils;
using Phantasma.Cryptography;
using Phantasma.Storage.Context;

namespace Phantasma.Blockchain.Tokens
{
    public struct BalanceSheet
    {
        private byte[] _prefix;

        public BalanceSheet(string symbol)
        {
            this._prefix = MakePrefix(symbol);
        }

        public static byte[] MakePrefix(string symbol)
        {
            var key = $".balances.{symbol}";
            return Encoding.ASCII.GetBytes(key);
        }

        private byte[] GetKeyForAddress(Address address)
        {
            return ByteArrayUtils.ConcatBytes(_prefix, address.ToByteArray());
        }

        public BigInteger Get(StorageContext storage, Address address)
        {
            lock (storage)
            {
                var key = GetKeyForAddress(address);
                var temp = storage.Get(key); // TODO make utils method GetBigInteger
                if (temp == null || temp.Length == 0)
                {
                    return 0;
                }
                return new BigInteger(temp);
            }
        }

        public bool Add(StorageContext storage, Address address, BigInteger amount)
        {
            if (amount <= 0)
            {
                return false;
            }

            var balance = Get(storage, address);
            balance += amount;

            var key = GetKeyForAddress(address);

            lock (storage)
            {
                storage.Put(key, balance);
            }

            return true;
        }

        public bool Subtract(StorageContext storage, Address address, BigInteger amount)
        {
            if (amount <= 0)
            {
                return false;
            }

            var balance = Get(storage, address);

            var diff = balance - amount;
            if (diff < 0)
            {
                return false;
            }

            balance -= amount;

            var key = GetKeyForAddress(address);

            lock (storage)
            {
                if (balance == 0)
                {
                    storage.Delete(key);
                }
                else
                {
                    storage.Put(key, balance);
                }
            }

            return true;
        }
    }
}
