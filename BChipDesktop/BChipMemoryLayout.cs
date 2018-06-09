﻿using bChipDesktop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.SmartCards;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace BChipDesktop
{
    /// 224 bytes free to write outside of the protected region
    /// Regions for v0a: 
    ///         Write Once Protected Area (unused): 0x00-0x20      (32 bytes)
    ///         ID Memory:        (0x20)+0x00-0x07                 (8 bytes)
    ///         Unused Memory:    (0x20)+0x08-0xFF                 (214 bytes)
    public abstract class BChipMemoryLayout
    {
        // 8 bytes - Memory Layout Version Identifier
        // NB: Initial release is v0a-XXYYYYYYYYYYYYYY
        //  The "XX" in an mlvi identifier is the bchip card type identifier
        //  The "YY" is the cards unique identifier
        // TODO: This will eventually be part of the ROM of the card and generated when shipped
        public const int MLVI_ADDR = 0;
        public const int MLVI_MAX_DATA = 8;
        public byte[] mlvi { get; }

        public BChipMemoryLayout(CardType cardType, byte[] cardId)
        {
            if (cardId == null || cardId.Length != 7)
            {
                throw new Exception("Card ID should be 7 bytes.");
            }

            this.cardType = cardType;
            this.mlvi = new byte[8];
            this.mlvi[0] = (byte)cardType;
            for (int i = 1; i < mlvi.Length; ++i)
            {
                this.mlvi[i] = cardId[i - 1];
            }
        }

        public BChipMemoryLayout(byte[] mlvi)
        {
            if (mlvi == null || mlvi.Length != 8)
            {
                throw new Exception("MLVI should be 8 bytes.");
            }

            this.cardType = (CardType)mlvi[0];
            this.mlvi = mlvi;
        }

        public abstract string IdLabel { get; }
        public virtual byte[] AddressCopyIcon
        {
            get
            {
                List<byte> copyIcon = new List<byte>();
                Stream imageStream = Assembly.GetEntryAssembly().GetManifestResourceStream(
                        "No-Copy-icon.png");
                int b = imageStream.ReadByte();
                while (b != -1)
                {
                    copyIcon.Add((byte)b);
                    b = imageStream.ReadByte();
                }
                return copyIcon.ToArray();
            }
        }
        public abstract string CardTypeLabel { get; }
        public abstract string PublicAddress { get; }
        public abstract bool IsConnected { get; set; }
        public abstract string ConnectionString { get; }
        public abstract string PkSource { get; }

        //public abstract PKType PkType { get; }
        public CardType cardType { get; private set; }

        public abstract Task<IBuffer> WriteDataToCard(Configuration cardReaderConfigInterface);

        /// <summary>
        /// Format's a BChip for use by the user. If no identifier is specified, a random one is created.
        /// NB: If the BChip carries its identifier in the ROM, it cannot be changed. If the card has had too many
        ///     wrong PIN attempts (3 bytes), it will either be permantly in READ ONLY mode or permanently disabled.
        /// </summary>
        public static async Task<IBuffer> FormatCard(
            Configuration cardReaderConfigInterface, 
            CardType cardType, 
            byte[] customIdentifier = null)
        {
            if (cardReaderConfigInterface.ValidateCardReaderConnected())
            {
                List<byte> mlvi = new List<byte>();
                mlvi.Add((byte)cardType);
                if (customIdentifier == null)
                {
                    mlvi.AddRange(CryptographicBuffer.GenerateRandom(7).ToArray());
                }
                else
                {
                    if (customIdentifier.Length != 7)
                    {
                        throw new FormatException("Custom identifier should be exactly 7 bytes of data.");
                    }
                    mlvi.AddRange(customIdentifier.ToArray());
                }

                // pad the list
                int padding = 0;
                switch (cardType)
                {
                    case CardType.BChip:
                        padding = (int)CardMemory.BChip;
                        break;
                }
                List<byte> paddedList = new List<byte>();
                if (mlvi.Count < padding)
                {
                    padding -= mlvi.Count;
                }
                for (int i = 0; i < padding; i++)
                {
                    paddedList.Add(0xFF);
                }

                // Connect to bchip
                SmartCard card = 
                    await cardReaderConfigInterface.GetSmartCard(
                        cardReaderConfigInterface.GetConnectedBChip()   );

                bool res = await UnlockSmartCard(cardReaderConfigInterface);

                await AdpuHandler.SendADPUCommand(card, AdpuCommand.WriteMlviData, mlvi.ToArray());

                return await AdpuHandler.SendADPUCommand(card, AdpuCommand.WriteCardData, paddedList.ToArray());
            }

            return null;
        }

        public static async Task<bool> UnlockSmartCard(Configuration cardReaderConfigInterface)
        {
            SmartCard card =
                await cardReaderConfigInterface.GetSmartCard(
                    cardReaderConfigInterface.GetConnectedBChip());

            IBuffer result = await AdpuHandler.SendADPUCommand(card, AdpuCommand.SendPinCode, cardReaderConfigInterface.BChipPinCode);

            return CryptographicBuffer.EncodeToHexString(result).EndsWith("9000");
        }
    }

    /// Regions for initial bChip release (v0a): 
    public class BChipMemoryLayout_BCHIP : BChipMemoryLayout
    {
        public BChipMemoryLayout_BCHIP(
            IBuffer mlvi,
            IBuffer cardData,
            bool isConnected,
            PKStatus pkStatus)
            : base(CardType.BChip, mlvi.ToArray())
        {
            uint len = cardData.Length;
            this.Salt = cardData.ToArray(SALT_ADDR, SALT_MAX_DATA);
            this.bchipVIDent = cardData.ToArray(VID_DATA_ADDR, VID_MAX_DATA);
            this.privateKeyData = cardData.ToArray(PK_DATA_ADDR, PRIVATEKEY_MAX_DATA);
            this.publicKeyData = cardData.ToArray(PUBKEY_DATA_ADDR, PUBKEY_MAX_DATA);
            this.crcData = cardData.ToArray(CRC_DATA_ADDR, CRC_MAX_SIZE);
            this.IsConnected = isConnected;
            this.PkStatus = pkStatus;
        }

        public BChipMemoryLayout_BCHIP( IBuffer mlvi ) : base(CardType.BChip, mlvi.ToArray())
        {
            this.Salt = new byte[SALT_MAX_DATA];
            for (int i = 0; i < SALT_MAX_DATA; i++)
            {
                this.Salt[i] = 0x55;
            }
            this.bchipVIDent = new byte[VID_MAX_DATA];
            this.bchipVIDent[0] = (int)PKType.CUSTOM;
            for (int i = 1; i < VID_MAX_DATA; i++)
            {
                this.bchipVIDent[i] = 0xBB;
            }
            this.privateKeyData = new byte[PRIVATEKEY_MAX_DATA];
            for (int i = 0; i < PRIVATEKEY_MAX_DATA; i++)
            {
                this.privateKeyData[i] = 0x00;
            }
            this.publicKeyData = new byte[PUBKEY_MAX_DATA];
            for (int i = 0; i < PUBKEY_MAX_DATA; i++)
            {
                this.publicKeyData[i] = 0xAA;
            }
            
            this.IsConnected = false;
            this.PkStatus = PKStatus.NotAvailable;
        }
        
        public override bool IsConnected { get; set; }
        public PKStatus PkStatus { get; private set; }

        public bool NotInitialized
        {
            get
            {
                return
                    // A card that shouldn't be written to, but will nuke all data.
                    (Salt.Where(a => a == 0x55).Count() == Salt.Length) &&
                    (privateKeyData.Where(a => a == 0x00).Count() == privateKeyData.Length);
            }
        }

        public bool IsFormatted
        {
            get
            {
                return
                    // Spec for original Bchip has card new card data 
                    // set to 0xFF when unconfigured
                    (Salt.Where(a => a == 0xFF).Count() == Salt.Length) &&
                    (bchipVIDent.Where(a => a == 0xFF).Count() == bchipVIDent.Length) &&
                    (privateKeyData.Where(a => a == 0xFF).Count() == privateKeyData.Length);
            }
        }
        
        // 8 bytes RNG - 
        public const int SALT_ADDR = 0;
        public const int SALT_MAX_DATA = 8;

        public byte[] Salt { get; private set; }
        // 32 bytes
        // 00-07: Private Key Type Identifier (Private key source)
        // 01   : PK length (32, 64 or 96)
        //        See PKType enum. Bytes 1-7 unused.
        public const int VID_PKTYPE_ADDR = 0;
        public const int VID_PKLEN_ADDR = 1;
        // 08-15: Build version identifier
        public const int VID_BUILD_VERSION = 8;
        // 16-23: Reserved for future use
        public const int VID_RSVP = 16;
        public const int VID_DATA_ADDR = SALT_ADDR + SALT_MAX_DATA; // 8+0==8
        public const int VID_MAX_DATA = 24;
        public byte[] bchipVIDent { get; }

        // Can hold a maximum of 64 bytes of public key data
        public const int PUBKEY_DATA_ADDR = VID_DATA_ADDR + VID_MAX_DATA; // 8+32==40
        public const int PUBKEY_MAX_DATA = 64;
        public byte[] publicKeyData { get; private set; }

        public const int PK_DATA_ADDR = PUBKEY_DATA_ADDR + PUBKEY_MAX_DATA; // 40+64==104
        // maximum bytes that can be encrypted on a BChip card
        // Most private keys are either 32 or 64 bytes, such as deterministic wallets,
        // and some keys supporting up to 512bits, for a total of 96 bytes. 
        public const int MAX_USER_PK = 96;
        // The total size of the encrypted key. The key size is "always" 96 bytes, + padding, is 112.
        public const int PRIVATEKEY_MAX_DATA = 112;
        public byte[] privateKeyData { get; private set; }

        // 104+112==216 -> 7 bytes remaining
        public const int CRC_DATA_ADDR = PK_DATA_ADDR + PRIVATEKEY_MAX_DATA;
        public const int CRC_MAX_SIZE = 7;
        public byte[] crcData { get; private set; }

        /// <summary>
        /// Takes in a passphrase, a salt and private key - stores the encrypted bits locally, publicKey is optional.
        /// TODO: This method will be deprecated sooner than later for a more secure alternative.
        /// </summary>
        public async void EncryptPrivateKeyData(
            PKType keyType, 
            string passPhrase, 
            byte[] privateKey, 
            byte[] publicKey,
            Configuration physicalCard)
        {
            if (privateKey.Length > 96)
            {
                throw new Exception("Private Key length was larger than 96 bytes.");
            }

            byte[] dataToEncrypt = new byte[MAX_USER_PK];
            for(int i = 0; i < MAX_USER_PK; ++i)
            {
                if (i < privateKey.Length)
                {
                    dataToEncrypt[i] = privateKey[i];
                }
                else
                {
                    dataToEncrypt[i] = 0xFF;
                }
            }

            bchipVIDent[VID_PKLEN_ADDR] = (byte)privateKey.Length;

            // Quick and dirty
            int maxKeys = 256;

            // Generate keys:
            // This is the initial version and meant for speed. Eventually, we will force the
            // client to go through a large number of potential passfords as a form of POW
            string pass = passPhrase.ToString();
            var potPasswords = Encryptor.GeneratePassword(pass, maxKeys);
            byte[] initialPassword = Encryptor.CalculateSha512(CryptographicBuffer.ConvertStringToBinary(pass, BinaryStringEncoding.Utf8).ToArray());
            int generatedPin = Encryptor.GeneratePinCode(initialPassword, maxKeys);

            // Selected a key, Generate IV 
            IBuffer chosenPassword = potPasswords[generatedPin % potPasswords.Count].AsBuffer();
            // Nuke the salt *every* time
            this.Salt = CryptographicBuffer.GenerateRandom(SALT_MAX_DATA).ToArray();
            IBuffer chosenSalt = Encryptor.GenerateSalt(this.Salt).AsBuffer();
            this.privateKeyData = Encryptor.Encrypt(dataToEncrypt.AsBuffer(), chosenPassword, chosenSalt);
            this.PkType = keyType;

            byte[] publicKeyData = new byte[PUBKEY_MAX_DATA];
            int pubKeyLen = 0;
            if (publicKey != null)
            {
                pubKeyLen = publicKey.Length;
            }
            for (int i = 0; i < publicKeyData.Length; ++i)
            {
                if (i < pubKeyLen)
                {
                    publicKeyData[i] = publicKey[i];
                }
                else
                {
                    publicKeyData[i] = 0xFF;
                }
            }

            this.publicKeyData = publicKeyData;

            this.crcData = GetCardCheckSum();

            if (physicalCard != null)
            {
                await WriteDataToCard(physicalCard);
            }
        }

        public byte[] DecryptPrivateKeyData(string passPhrase)
        {
            int expectedLength = bchipVIDent[VID_PKLEN_ADDR];

            if (expectedLength > 96)
            {
                throw new Exception("Private Key length was larger than 96 bytes.");
            }

            // Quick and dirty
            int maxKeys = 256;

            // Generate keys
            var potPasswords = Encryptor.GeneratePassword(passPhrase, maxKeys);
            byte[] initialPassword = Encryptor.CalculateSha512(CryptographicBuffer.ConvertStringToBinary(passPhrase, BinaryStringEncoding.Utf8).ToArray());
            int generatedPin = Encryptor.GeneratePinCode(initialPassword, maxKeys);

            // Selected a key, Regenerate IV 
            IBuffer chosenPassword = potPasswords[generatedPin % potPasswords.Count].AsBuffer();
            IBuffer chosenSalt = Encryptor.GenerateSalt(this.Salt).AsBuffer();

            byte[] decryptedData = Encryptor.Decrypt(this.privateKeyData.AsBuffer(), chosenPassword, chosenSalt);

            if (decryptedData == null || decryptedData.Length == 0)
            {
                return null;
            }

            byte[] parsedKeyData = new byte[expectedLength];
            for (int i = 0; i < parsedKeyData.Length; ++i)
            {
                parsedKeyData[i] = decryptedData[i];
            }

            return parsedKeyData;
        }

        public byte[] GetCardCheckSum()
        {
            List<byte> cardBytes = new List<byte>();
            cardBytes.AddRange(this.Salt);
            cardBytes.AddRange(this.bchipVIDent);
            cardBytes.AddRange(this.publicKeyData);
            cardBytes.AddRange(this.privateKeyData);

            byte[] calc = SHA256.Create().ComputeHash(cardBytes.ToArray());
            calc = SHA256.Create().ComputeHash(calc);

            byte[] checksum = new byte[CRC_MAX_SIZE];
            for (int i = 0; i<checksum.Length; ++i)
            {
                checksum[i] = calc[i];
            }

            return checksum;
        }

        public override async Task<IBuffer> WriteDataToCard(Configuration cardReaderConfigInterface)
        {
            if (cardReaderConfigInterface.ValidateCardReaderConnected())
            {
                List<byte> dataToWrite = new List<byte>();
                
                if (mlvi.Length != 8)
                {
                    throw new FormatException("MLVI should be exactly 8 bytes of data. Was the card properly configured?");
                }
                
                dataToWrite.AddRange(Salt);
                // Always replace the build version bits for backcompat safety 
                System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                System.Reflection.AssemblyName assemblyName = assembly.GetName();
                Version version = assemblyName.Version;
                bchipVIDent[VID_BUILD_VERSION + 0] = (byte)version.MajorRevision;
                bchipVIDent[VID_BUILD_VERSION + 1] = (byte)version.MinorRevision;
                bchipVIDent[VID_BUILD_VERSION + 2] = (byte)version.Build;
                bchipVIDent[VID_BUILD_VERSION + 3] = (byte)version.Revision;

                dataToWrite.AddRange(this.bchipVIDent);
                dataToWrite.AddRange(this.publicKeyData);
                dataToWrite.AddRange(this.privateKeyData);
                this.crcData = GetCardCheckSum();
                dataToWrite.AddRange(this.crcData);
                
                // Connect to bchip
                SmartCard card =
                    await cardReaderConfigInterface.GetSmartCard(
                        cardReaderConfigInterface.GetConnectedBChip());

                bool unlockResult = await UnlockSmartCard(cardReaderConfigInterface);
                if (unlockResult)
                {
                    return await AdpuHandler.SendADPUCommand(card, AdpuCommand.WriteCardData, dataToWrite.ToArray());
                }
                else
                {
                    throw new Exception("Failed to unblock smart card for write access");
                }
            }

            return null;
        }

        public override string IdLabel
        {
            get
            {
                StringBuilder formattedId = new StringBuilder();
                for (int i = 0; i < mlvi.Length; i+=2)
                {
                    formattedId.Append($"{mlvi[i]:X}{mlvi[i+1]:X} ");
                }

                return formattedId.ToString();
            }
        }
        public override byte[] AddressCopyIcon
        {
            get
            {
                List<byte> copyIcon = new List<byte>();
                Stream imageStream = Assembly.GetEntryAssembly().GetManifestResourceStream(
                        "BChipDesktop.Assets.Editing-Copy-icon.png");
                int b = imageStream.ReadByte();
                while (b != -1)
                {
                    copyIcon.Add((byte)b);
                    b = imageStream.ReadByte();
                }
                return copyIcon.ToArray();
            }
        }

        public PKType PkType
        {
            get
            {
                return (PKType)bchipVIDent[BChipMemoryLayout_BCHIP.VID_PKTYPE_ADDR];
            }
            set
            {
                bchipVIDent[BChipMemoryLayout_BCHIP.VID_PKTYPE_ADDR] = (byte)value;
            }
        }

        public override string CardTypeLabel
        {
            get
            {
                try
                {
                    PKType detectedPkType = (PKType)bchipVIDent[BChipMemoryLayout_BCHIP.VID_PKTYPE_ADDR];
                    return detectedPkType.ToString();
                }
                catch
                {
                    return "UNSUPPORTED";
                }
            }
        }
        public override string PublicAddress
        { get
            {
                return "Not implemented";
            }
        }
        public override string ConnectionString
        { get
            {
                return IsConnected?"Connected":"Not Connected";
            }
        }
        public override string PkSource
        { get
            {
                return PkStatus.ToString();
            }
        }
    }
}