using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NBitcoin;
using Stratis.Bitcoin.Features.Wallet;
using Stratis.SmartContracts.CLR;
using Stratis.SmartContracts.CLR.Serialization;
using Stratis.SmartContracts.Core;
using Stratis.SmartContracts.RuntimeObserver;
using Unity3dApi;
using UnityEngine;
using Network = NBitcoin.Network;

public class StratisUnityManager
{
    public readonly Unity3dClient Client;

    private Network network;

    private Mnemonic mnemonic;

    private Key privateKey;

    private PubKey publicKey;

    private BitcoinPubKeyAddress address;

    // Smart contract related
    public ulong GasPrice { get; set; } = 100;

    public ulong GasLimit { get; set; } = 150000;

    public Money DefaultFee = "0.0001";

    private MethodParameterStringSerializer methodParameterStringSerializer;

    private CallDataSerializer callDataSerializer;

    public StratisUnityManager(Unity3dClient client, Network network, Mnemonic mnemonic)
    {
        this.Client = client;
        this.network = network;
        this.mnemonic = mnemonic;

        this.privateKey = this.mnemonic.DeriveExtKey().PrivateKey;
        this.publicKey = this.privateKey.PubKey;
        this.address = this.publicKey.GetAddress(network);

        this.methodParameterStringSerializer = new MethodParameterStringSerializer(this.network);
        this.callDataSerializer = new CallDataSerializer(new ContractPrimitiveSerializer(this.network));
    }

    public async Task<decimal> GetBalanceAsync()
    {
        long balanceSat = await Client.GetAddressBalanceAsync(this.address.ToString()).ConfigureAwait(false);

        decimal balance = new Money(balanceSat).ToUnit(MoneyUnit.BTC);

        return balance;
    }

    public BitcoinPubKeyAddress GetAddress()
    {
        return this.address;
    }

    public async Task<string> SendTransactionAsync(string destinationAddress, Money sendAmount)
    {
        Coin[] coins = await this.GetCoinsAsync().ConfigureAwait(false);

        BitcoinPubKeyAddress addrTo = new BitcoinPubKeyAddress(destinationAddress, this.network);
    
        var txBuilder = new TransactionBuilder(this.network);
        Transaction tx = txBuilder
            .AddCoins(coins)
            .AddKeys(this.privateKey)
            .Send(addrTo, sendAmount)
            .SendFees(DefaultFee)
            .SetChange(this.address)
            .BuildTransaction(true);
    
        if (!txBuilder.Verify(tx))
            Debug.LogError("Tx wasn't fully signed!");

        Debug.Log(string.Format("Created tx {0} to {1}, amount: {2}.", tx.GetHash(), destinationAddress, sendAmount));

        await Client.SendTransactionAsync(new SendTransactionRequest() {Hex = tx.ToHex()}).ConfigureAwait(false);

        Debug.Log("Transaction sent.");
        return tx.GetHash().ToString();
    }

    public async Task<string> SendOpReturnTransactionAsync(string opReturnData)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(opReturnData);

        return await this.SendOpReturnTransactionAsync(bytes).ConfigureAwait(false);
    }

    public async Task<string> SendOpReturnTransactionAsync(byte[] bytes)
    {
        Coin[] coins = await this.GetCoinsAsync().ConfigureAwait(false);

        Script opReturnScript = TxNullDataTemplate.Instance.GenerateScriptPubKey(bytes);

        var txBuilder = new TransactionBuilder(this.network);
        Transaction tx = txBuilder
            .AddCoins(coins)
            .AddKeys(this.privateKey)
            .Send(opReturnScript, Money.Zero)
            .SendFees(DefaultFee)
            .SetChange(this.address)
            .BuildTransaction(true);

        if (!txBuilder.Verify(tx))
            Debug.LogError("Tx wasn't fully signed!");

        Debug.Log(string.Format("Created OP_RETURN tx {0}, data: {1}.", tx.GetHash(), Encoding.UTF8.GetString(bytes)));

        await Client.SendTransactionAsync(new SendTransactionRequest() { Hex = tx.ToHex() }).ConfigureAwait(false);

        Debug.Log("Transaction sent.");
        return tx.GetHash().ToString();
    }

    private async Task<Coin[]> GetCoinsAsync()
    {
        GetUTXOsResponseModel utxos = await Client.GetUTXOsForAddressAsync(this.address.ToString()).ConfigureAwait(false);

        Coin[] coins = utxos.Utxos.Select(x => new Coin(new OutPoint(uint256.Parse(x.Hash), x.N), new TxOut(new Money(x.Satoshis), address))).ToArray();

        return coins;
    }

    /// <summary>
    /// Creates and sends smart contract create call.
    /// </summary>
    /// <param name="contractCode">Compiled contract code represented as hex string.</param>
    /// <param name="parameters">An array of encoded strings containing the parameters (and their type) to pass to the smart contract
    /// constructor when it is called. More information on the format of a parameter string is available
    /// <a target="_blank" href="https://academy.stratisplatform.com/SmartContracts/working-with-contracts.html#parameter-serialization">here</a>.</param>
    public async Task<string> SendCreateContractTransactionAsync(string contractCode, string[] parameters = null, Money amount = null)
    {
        Coin[] coins = await this.GetCoinsAsync().ConfigureAwait(false);

        ContractTxData txData;
        if (parameters != null && parameters.Any())
        {
            object[] methodParameters = methodParameterStringSerializer.Deserialize(parameters);
            txData = new ContractTxData(1, (Gas)GasPrice, (Gas)GasLimit, contractCode.HexToByteArray(), methodParameters);
        }
        else
        {
            txData = new ContractTxData(1, (Gas)GasPrice, (Gas)GasLimit, contractCode.HexToByteArray());
        }

        ulong totalFee = (Gas)GasPrice * (Gas)GasLimit + DefaultFee;

        byte[] serializedTxData = this.callDataSerializer.Serialize(txData);

        ContractTxData deserialized = this.callDataSerializer.Deserialize(serializedTxData);

        var txBuilder = new TransactionBuilderSC(this.network);
        Transaction tx = txBuilder
            .AddCoins(coins)
            .AddKeys(this.privateKey)
            .Send(new Script(serializedTxData), amount ?? Money.Zero, true)
            .SendFees(totalFee)
            .SetChange(this.address)
            .BuildTransaction(true);
        
        await Client.SendTransactionAsync(new SendTransactionRequest() { Hex = tx.ToHex() }).ConfigureAwait(false);

        Debug.Log("Transaction sent.");
        return tx.GetHash().ToString();
    }

    /// <summary>
    /// Creates and sends smart contract call.
    /// </summary>
    /// <param name="contractAddr">Contract address</param>
    /// <param name="methodName">Method name to call</param>
    /// <param name="parameters">Call parameters</param>
    /// <param name="amount">Money value to attach to call</param>
    /// <returns></returns>
    public async Task<string> SendCallContractTransactionAsync(string contractAddr, string methodName, string[] parameters = null, Money amount = null)
    {
        Coin[] coins = await this.GetCoinsAsync().ConfigureAwait(false);

        uint160 addressNumeric = contractAddr.ToUint160(this.network);

        ContractTxData txData = null;
        if (parameters != null && parameters.Any())
        {
            object[] methodParameters = this.methodParameterStringSerializer.Deserialize(parameters);
            txData = new ContractTxData(1, (Gas)GasPrice, (Gas)GasLimit, addressNumeric, methodName, methodParameters);
        }

        ulong totalFee = (Gas)GasPrice * (Gas)GasLimit + DefaultFee;

        byte[] serializedTxData = this.callDataSerializer.Serialize(txData);

        ContractTxData deserialized = this.callDataSerializer.Deserialize(serializedTxData);

        var txBuilder = new TransactionBuilderSC(this.network);
        Transaction tx = txBuilder
            .AddCoins(coins)
            .AddKeys(this.privateKey)
            .Send(new Script(serializedTxData), amount ?? Money.Zero, true)
            .SendFees(totalFee)
            .SetChange(this.address)
            .BuildTransaction(true);
        await Client.SendTransactionAsync(new SendTransactionRequest() { Hex = tx.ToHex() }).ConfigureAwait(false);

        Debug.Log("SC call transaction sent. Id: " + tx.GetHash().ToString());
        return tx.GetHash().ToString();
    }

    public async Task<ReceiptResponse> WaitTillReceiptAvailable(string txId)
    {
        while (true)
        {
            ReceiptResponse result = await this.Client.ReceiptAsync(txId).ConfigureAwait(false);

            if (result != null)
                return result;

            Debug.Log("Waiting for receipt...");
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }
}
