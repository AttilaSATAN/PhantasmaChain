﻿using System;
using System.Numerics;
using Phantasma.Numerics;
using Phantasma.Core.Types;
using Phantasma.Cryptography;
using Phantasma.Domain;
using Phantasma.Storage;
using Phantasma.Storage.Context;

namespace Phantasma.Blockchain.Contracts
{
    [Flags]
    public enum SaleFlags
    {
        None = 0,
        Whitelist = 1,
    }

    public enum SaleEventKind
    {
        Creation,
        SoftCap,
        HardCap,
        AddedToWhitelist,
        RemovedFromWhitelist,
        Completion,
        Refund
    }

    public struct SaleEventData
    {
        public Hash saleHash;
        public SaleEventKind kind;
    }

    public struct SaleInfo
    {
        public Address Creator;
        public string Name;
        public SaleFlags Flags;
        public Timestamp StartDate;
        public Timestamp EndDate;

        public string SellSymbol;
        public string ReceiveSymbol;
        public BigInteger Price;
        public BigInteger SoftCap;
        public BigInteger HardCap;
        public BigInteger UserLimit;
    }

    public sealed class SaleContract : NativeContract
    {
        public override NativeContractKind Kind => NativeContractKind.Sale;

        internal StorageMap _saleMap; //<Hash, Collection<StorageEntry>>
        internal StorageMap _buyerAmounts; //<Hash, Collection<StorageEntry>>
        internal StorageMap _buyerAddresses; //<Hash, Collection<StorageEntry>>
        internal StorageMap _whitelistedAddresses; //<Hash, Collection<StorageEntry>>
        internal StorageList _saleList; //List<Hash>
        internal StorageMap _saleSupply; //Map<Hash, BigInteger>


        public SaleContract() : base()
        {
        }

        public Hash CreateSale(Address from, string name, SaleFlags flags, Timestamp startDate, Timestamp endDate, string sellSymbol, string receiveSymbol, BigInteger price, BigInteger softCap, BigInteger hardCap, BigInteger userLimit)
        {
            Runtime.Expect(Runtime.IsWitness(from), "invalid witness");

            var minPrice = UnitConversion.ToBigInteger(0.001m, DomainSettings.FiatTokenDecimals);

            Runtime.Expect(Runtime.TokenExists(sellSymbol), "token must exist: " + sellSymbol);

            var token = Runtime.GetToken(sellSymbol);
            Runtime.Expect(token.IsFungible(), "token must be fungible: " + sellSymbol);
            Runtime.Expect(token.IsTransferable(), "token must be transferable: " + sellSymbol);

            Runtime.Expect(price >= minPrice, "invalid price");
            Runtime.Expect(softCap >= 0, "invalid softcap");
            Runtime.Expect(hardCap > 0, "invalid hard cap");
            Runtime.Expect(hardCap >= softCap, "hard cap must be larger or equal to soft capt");
            Runtime.Expect(userLimit >= 0, "invalid user limit");

            Runtime.Expect(receiveSymbol != sellSymbol, "invalid receive token symbol: " + receiveSymbol);


            // TODO remove this later when Cosmic Swaps 2.0 are released
            Runtime.Expect(receiveSymbol == DomainSettings.StakingTokenSymbol, "invalid receive token symbol: " + receiveSymbol); 

            Runtime.TransferTokens(sellSymbol, from, this.Address, hardCap);

            var sale = new SaleInfo()
            {
                Creator = from,
                Name = name,
                Flags = flags,
                StartDate = startDate,
                EndDate = endDate,
                SellSymbol = sellSymbol,
                ReceiveSymbol = receiveSymbol,
                Price = price,
                SoftCap = softCap,
                HardCap = hardCap,
                UserLimit = userLimit
            };

            var bytes = Serialization.Serialize(sale);
            var hash = Hash.FromBytes(bytes);

            _saleList.Add(hash);
            _saleMap.Set(hash, sale);
            _saleSupply.Set(hash, 0);

            Runtime.Notify(EventKind.SaleMilestone, from, new SaleEventData() { kind = SaleEventKind.Creation, saleHash = hash });

            return hash;
        }

        public bool IsSaleActive(Hash saleHash)
        {
            if (_saleMap.ContainsKey(saleHash))
            {
                var sale = _saleMap.Get<Hash, SaleInfo>(saleHash);

                if (Runtime.Time < sale.StartDate)
                {
                    return false;
                }

                if (Runtime.Time > sale.EndDate)
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        public SaleInfo[] GetSales()
        {
            return _saleList.All<SaleInfo>();
        }

        public Address[] GetSaleParticipants(Hash saleHash)
        {
            var addressMap = _buyerAddresses.Get<Hash, StorageSet>(saleHash);
            return addressMap.AllValues<Address>();
        }

        public Address[] GetSaleWhitelists(Hash saleHash)
        {
            var addressMap = _whitelistedAddresses.Get<Hash, StorageSet>(saleHash);
            return addressMap.AllValues<Address>();
        }

        public bool IsWhitelisted(Hash saleHash, Address address)
        {
            var addressMap = _whitelistedAddresses.Get<Hash, StorageSet>(saleHash);

            return addressMap.Contains<Address>(address);
        }

        public void AddToWhitelist(Hash saleHash, Address target)
        {
            Runtime.Expect(IsSaleActive(saleHash), "sale not active or does not exist");

            var sale = _saleMap.Get<Hash, SaleInfo>(saleHash);
            Runtime.Expect(sale.Flags.HasFlag(SaleFlags.Whitelist), "this sale is not using whitelists");

            Runtime.Expect(Runtime.IsWitness(sale.Creator), "invalid witness");

            var addressMap = _whitelistedAddresses.Get<Hash, StorageSet>(saleHash);

            if (!addressMap.Contains<Address>(target))
            {
                addressMap.Add<Address>(target);
                Runtime.Notify(EventKind.SaleMilestone, target, new SaleEventData() { kind = SaleEventKind.AddedToWhitelist, saleHash = saleHash });
            }
        }

        public void RemoveFromWhitelist(Hash saleHash, Address target)
        {
            Runtime.Expect(IsSaleActive(saleHash), "sale not active or does not exist");

            var sale = _saleMap.Get<Hash, SaleInfo>(saleHash);
            Runtime.Expect(sale.Flags.HasFlag(SaleFlags.Whitelist), "this sale is not using whitelists");

            Runtime.Expect(Runtime.IsWitness(sale.Creator), "invalid witness");

            var addressMap = _whitelistedAddresses.Get<Hash, StorageSet>(saleHash);

            if (addressMap.Contains<Address>(target))
            {
                addressMap.Remove<Address>(target);
                Runtime.Notify(EventKind.SaleMilestone, target, new SaleEventData() { kind = SaleEventKind.RemovedFromWhitelist, saleHash = saleHash });
            }
        }

        public BigInteger GetPurchasedAmount(Hash saleHash, Address address)
        {
            var amountMap = _buyerAmounts.Get<Hash, StorageMap>(saleHash);
            var totalAmount = amountMap.Get<Address, BigInteger>(address);
            return totalAmount;
        }

        public void Purchase(Address from, Hash saleHash, string quoteSymbol, BigInteger quoteAmount)
        {
            Runtime.Expect(Runtime.TokenExists(quoteSymbol), "token must exist: " + quoteSymbol);
            var quoteToken = Runtime.GetToken(quoteSymbol);

            Runtime.Expect(IsSaleActive(saleHash), "sale not active or does not exist");

            var sale = _saleMap.Get<Hash, SaleInfo>(saleHash);
            Runtime.Expect(quoteSymbol != sale.SellSymbol, "cannot participate in the sale using " + quoteSymbol);

            Runtime.Expect(Runtime.IsWitness(from), "invalid witness");
            if (sale.Flags.HasFlag(SaleFlags.Whitelist))
            {
                Runtime.Expect(IsWhitelisted(saleHash, from), "address is not whitelisted");
            }

            var saleToken = Runtime.GetToken(sale.SellSymbol);
            var convertedAmount = Runtime.ConvertQuoteToBase(quoteAmount, sale.Price, saleToken, quoteToken);

            var temp = UnitConversion.ToDecimal(convertedAmount, saleToken.Decimals);
            Runtime.Expect(temp >= 1, "cannot purchase very tiny amount");

            var previousSupply = _saleSupply.Get<Hash, BigInteger>(saleHash);
            var nextSupply = previousSupply + convertedAmount;

            //Runtime.Expect(nextSupply <= sale.HardCap, "hard cap reached");
            if (nextSupply > sale.HardCap)
            {
                convertedAmount = sale.HardCap - previousSupply;
                Runtime.Expect(convertedAmount > 0, "hard cap reached");
                quoteAmount = Runtime.ConvertBaseToQuote(convertedAmount, sale.Price, saleToken, quoteToken);
                nextSupply = 0;
            }

            if (nextSupply == 0)
            {
                Runtime.Notify(EventKind.SaleMilestone, from, new SaleEventData() { kind = SaleEventKind.HardCap, saleHash = saleHash });
            }
            else
            if (previousSupply < sale.SoftCap && nextSupply >= sale.SoftCap)
            {
                Runtime.Notify(EventKind.SaleMilestone, from, new SaleEventData() { kind = SaleEventKind.SoftCap, saleHash = saleHash });
            }

            Runtime.TransferTokens(quoteSymbol, from, this.Address, quoteAmount);

            if (quoteSymbol != sale.ReceiveSymbol)
            {
                Runtime.CallNativeContext(NativeContractKind.Swap, nameof(SwapContract.SwapTokens), this.Address, quoteSymbol, sale.ReceiveSymbol, quoteAmount);
            }

            var amountMap = _buyerAmounts.Get<Hash, StorageMap>(saleHash);
            var totalAmount = amountMap.Get<Address, BigInteger>(from);

            if (sale.UserLimit > 0)
            {
                Runtime.Expect(totalAmount + convertedAmount <= sale.UserLimit, "user purchase limit exceeded");
            }

            var addressMap = _buyerAddresses.Get<Hash, StorageSet>(saleHash);
            addressMap.Add<Address>(from);
        }

        // anyone can call this, not only manager, in order to be able to trigger refunds
        public void CloseSale(Address from, Hash saleHash)
        {
            Runtime.Expect(Runtime.IsWitness(from), "invalid witness");


            Runtime.Expect(_saleMap.ContainsKey(saleHash), "sale does not exist or already closed");

            var sale = _saleMap.Get<Hash, SaleInfo>(saleHash);

            Runtime.Expect(Runtime.Time > sale.EndDate, "sale still not reached end date");

            var soldSupply = _saleSupply.Get<Hash, BigInteger>(saleHash);
            var buyerAddresses = GetSaleParticipants(saleHash);

            var amountMap = _buyerAmounts.Get<Hash, StorageMap>(saleHash);

            var saleToken = Runtime.GetToken(sale.SellSymbol);
            var receiveToken = Runtime.GetToken(sale.ReceiveSymbol);

            if (soldSupply >= sale.SoftCap) // if at least soft cap reached, send tokens to buyers and funds to sellers
            {
                foreach (var buyer in buyerAddresses)
                {
                    var amount = amountMap.Get<Address, BigInteger>(buyer);
                    Runtime.TransferTokens(sale.SellSymbol, this.Address, buyer, amount);
                }

                var fundsAmount = Runtime.ConvertBaseToQuote(soldSupply, sale.Price, saleToken, receiveToken);
                Runtime.TransferTokens(sale.ReceiveSymbol, this.Address, sale.Creator, fundsAmount);
            }
            else // otherwise return funds to buyers and return tokens to sellers
            {
                foreach (var buyer in buyerAddresses)
                {
                    var amount = amountMap.Get<Address, BigInteger>(buyer);

                    amount = Runtime.ConvertBaseToQuote(amount, sale.Price, saleToken, receiveToken);
                    Runtime.TransferTokens(sale.ReceiveSymbol, this.Address, buyer, amount);
                }

                Runtime.TransferTokens(sale.SellSymbol, this.Address, sale.Creator, sale.HardCap);
            }
        }
    }
}
