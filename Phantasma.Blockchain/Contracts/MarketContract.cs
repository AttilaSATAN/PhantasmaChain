using Phantasma.Core.Types;
using Phantasma.Cryptography;
using Phantasma.Domain;
using Phantasma.Numerics;
using Phantasma.Storage.Context;
using Phantasma.VM;
using System;

namespace Phantasma.Blockchain.Contracts
{
    public struct MarketEventData
    {
        public string BaseSymbol;
        public string QuoteSymbol;
        public BigInteger ID;
        public BigInteger Price;
    }

    public struct MarketAuction
    {
        public readonly Address Creator;
        public readonly Timestamp StartDate;
        public readonly Timestamp EndDate;
        public readonly string BaseSymbol;
        public readonly string QuoteSymbol;
        public readonly BigInteger TokenID;
        public readonly BigInteger Price;

        public MarketAuction(Address creator, Timestamp startDate, Timestamp endDate, string baseSymbol, string quoteSymbol, BigInteger tokenID, BigInteger price)
        {
            Creator = creator;
            StartDate = startDate;
            EndDate = endDate;
            BaseSymbol = baseSymbol;
            QuoteSymbol = quoteSymbol;
            TokenID = tokenID;
            Price = price;
        }
    }

    public sealed class MarketContract : NativeContract
    {
        public override NativeContractKind Kind => NativeContractKind.Market;

        internal StorageMap _auctionMap; //<string, MarketAuction>
        internal StorageMap _auctionIds; //<string, MarketAuction>

        public MarketContract() : base()
        {
        }

        public void SellToken(Address from, string baseSymbol, string quoteSymbol, BigInteger tokenID, BigInteger price, Timestamp endDate)
        {
            Runtime.Expect(Runtime.IsWitness(from), "invalid witness");
            Runtime.Expect(endDate > Runtime.Time, "invalid end date");

            var maxAllowedDate = Runtime.Time + TimeSpan.FromDays(30);
            Runtime.Expect(endDate <= maxAllowedDate, "end date is too distant, max: " + maxAllowedDate + ", received: " + endDate);

            Runtime.Expect(Runtime.TokenExists(quoteSymbol), "invalid quote token");
            var quoteToken = Runtime.GetToken(quoteSymbol);
            Runtime.Expect(quoteToken.Flags.HasFlag(TokenFlags.Fungible), "quote token must be fungible");

            Runtime.Expect(Runtime.TokenExists(baseSymbol), "invalid base token");
            var baseToken = Runtime.GetToken(baseSymbol);
            Runtime.Expect(!baseToken.Flags.HasFlag(TokenFlags.Fungible), "base token must be non-fungible");

            var nft = Runtime.ReadToken(baseSymbol, tokenID);
            Runtime.Expect(nft.CurrentChain == Runtime.Chain.Name, "token not currently in this chain");
            Runtime.Expect(nft.CurrentOwner == from, "invalid owner");

            Runtime.TransferToken(baseToken.Symbol, from, this.Address, tokenID);

            var auction = new MarketAuction(from, Runtime.Time, endDate, baseSymbol, quoteSymbol, tokenID, price);
            var auctionID = baseSymbol + "." + tokenID;
            _auctionMap.Set(auctionID, auction);
            _auctionIds.Set(auctionID, auctionID);

            Runtime.Notify(EventKind.OrderCreated, from, new MarketEventData() { ID = tokenID, BaseSymbol = baseSymbol, QuoteSymbol = quoteSymbol, Price = price });
        }

        public void BuyToken(Address from, string symbol, BigInteger tokenID)
        {
            Runtime.Expect(Runtime.IsWitness(from), "invalid witness");

            var auctionID = symbol + "." + tokenID;

            Runtime.Expect(_auctionMap.ContainsKey<string>(auctionID), "invalid auction");
            var auction = _auctionMap.Get<string, MarketAuction>(auctionID);

            EndSaleInternal(from, symbol, tokenID, auction);

            Runtime.Notify(EventKind.OrderFilled, from, new MarketEventData() { ID = auction.TokenID, BaseSymbol = auction.BaseSymbol, QuoteSymbol = auction.QuoteSymbol, Price = auction.Price });
        }

        public void CancelSale(string symbol, BigInteger tokenID)
        {
            var auctionID = symbol + "." + tokenID;

            Runtime.Expect(_auctionMap.ContainsKey<string>(auctionID), "invalid auction");
            var auction = _auctionMap.Get<string, MarketAuction>(auctionID);

            var from = auction.Creator;

            if (Runtime.Time < auction.EndDate)
            {
                Runtime.Expect(Runtime.IsWitness(from), "invalid witness");
            }

            EndSaleInternal(from, symbol, tokenID, auction);

            Runtime.Notify(EventKind.OrderCancelled, from, new MarketEventData() { ID = auction.TokenID, BaseSymbol = auction.BaseSymbol, QuoteSymbol = auction.QuoteSymbol, Price = 0 });
        }

        private void EndSaleInternal(Address from, string symbol, BigInteger tokenID, MarketAuction auction)
        {
            Runtime.Expect(Runtime.TokenExists(auction.BaseSymbol), "invalid base token");
            var baseToken = Runtime.GetToken(auction.BaseSymbol);
            Runtime.Expect(!baseToken.Flags.HasFlag(TokenFlags.Fungible), "token must be non-fungible");

            var nft = Runtime.ReadToken(symbol, tokenID);
            Runtime.Expect(nft.CurrentChain == Runtime.Chain.Name, "token not currently in this chain");
            Runtime.Expect(nft.CurrentOwner == this.Address, "invalid owner");

            if (auction.Creator != from)
            {
                Runtime.Expect(Runtime.TokenExists(auction.QuoteSymbol), "invalid quote token");
                var quoteToken = Runtime.GetToken(auction.QuoteSymbol);
                Runtime.Expect(quoteToken.Flags.HasFlag(TokenFlags.Fungible), "quote token must be fungible");

                var balance = Runtime.GetBalance(quoteToken.Symbol, from);
                Runtime.Expect(balance >= auction.Price, $"not enough {quoteToken.Symbol} balance at {from.Text}");

                var finalAmount = auction.Price;

                if (Runtime.ProtocolVersion >= 4)
                {
                    var nftSymbol = auction.BaseSymbol;
                    var nftData = Runtime.ReadToken(nftSymbol, tokenID);
                    var series = Runtime.GetTokenSeries(nftSymbol, nftData.SeriesID);

                    var royaltyProperty = new ContractMethod("getRoyalties", VMType.Number, -1);

                    if (series.ABI.Implements(royaltyProperty))
                    {
                        var nftRoyalty = Runtime.CallNFT(nftSymbol, nftData.SeriesID, royaltyProperty, tokenID).AsNumber();
                        if (nftRoyalty > 50)
                        {
                            nftRoyalty = 50; // we don't allow more than 50% royalties fee
                        }
                        var royaltyFee = finalAmount * nftRoyalty / 100;
                        Runtime.TransferTokens(quoteToken.Symbol, from, nftData.Creator, royaltyFee);
                        finalAmount -= royaltyFee;
                    }
                }
                Runtime.TransferTokens(quoteToken.Symbol, from, auction.Creator, finalAmount);
            }

            Runtime.TransferToken(baseToken.Symbol, this.Address, from, auction.TokenID);

            var auctionID = symbol + "." + tokenID;
            _auctionMap.Remove<string>(auctionID);
            _auctionIds.Remove<string>(auctionID);
        }

        public MarketAuction[] GetAuctions()
        {
            var ids = _auctionIds.AllValues<string>();
            var auctions = new MarketAuction[ids.Length];
            for (int i = 0; i < auctions.Length; i++)
            {
                auctions[i] = _auctionMap.Get<string, MarketAuction>(ids[i]);
            }
            return auctions;
        }

        public bool HasAuction(string symbol, BigInteger tokenID)
        {
            var auctionID = symbol + "." + tokenID;
            return _auctionMap.ContainsKey<string>(auctionID);
        }

        public MarketAuction GetAuction(BigInteger tokenID)
        {
            Runtime.Expect(_auctionMap.ContainsKey<BigInteger>(tokenID), "invalid auction");
            var auction = _auctionMap.Get<BigInteger, MarketAuction>(tokenID);
            return auction;
        }
    }
}
