namespace Hilke.Ant.Protocol;

/// <summary>ANT message IDs used by this library (ANT Message Protocol &amp; Usage, D00000652).</summary>
public enum AntMessageId : byte
{
    UnassignChannel = 0x41,
    AssignChannel = 0x42,
    ChannelPeriod = 0x43,
    SearchTimeout = 0x44,
    RfFrequency = 0x45,
    NetworkKey = 0x46,
    TransmitPower = 0x47,
    ResetSystem = 0x4A,
    OpenChannel = 0x4B,
    CloseChannel = 0x4C,
    RequestMessage = 0x4D,
    BroadcastData = 0x4E,
    AcknowledgedData = 0x4F,
    BurstData = 0x50,
    ChannelId = 0x51,
    ChannelStatus = 0x52,
    Capabilities = 0x54,
    ChannelResponseEvent = 0x40,
    OpenRxScanMode = 0x5B,
    ChannelTransmitPower = 0x60,
    SerialNumber = 0x61,
    EnableExtRxMessages = 0x66,
    LibConfig = 0x6E,
    StartupMessage = 0x6F,
}

/// <summary>ANT channel type values (byte written in Assign Channel).</summary>
public enum ChannelType : byte
{
    BidirectionalSlave = 0x00,
    BidirectionalMaster = 0x10,
    SharedSlave = 0x20,
    SharedMaster = 0x30,
    SlaveReceiveOnly = 0x40,
    MasterTransmitOnly = 0x50,
}

/// <summary>Device-reported channel state (low 2 bits of Channel Status).</summary>
public enum DeviceChannelState : byte
{
    Unassigned = 0,
    Assigned = 1,
    Searching = 2,
    Tracking = 3,
}

/// <summary>Data payload classification for received data messages.</summary>
public enum DataKind : byte
{
    Broadcast,
    Acknowledged,
    Burst,
}

/// <summary>Channel response codes and RF event codes (Channel Response / Event message 0x40).</summary>
public enum ChannelResponseCode : byte
{
    ResponseNoError = 0x00,
    EventRxSearchTimeout = 0x01,
    EventRxFail = 0x02,
    EventTx = 0x03,
    EventTransferRxFailed = 0x04,
    EventTransferTxCompleted = 0x05,
    EventTransferTxFailed = 0x06,
    EventChannelClosed = 0x07,
    EventRxFailGoToSearch = 0x08,
    EventChannelCollision = 0x09,
    EventTransferTxStart = 0x0A,
    ChannelInWrongState = 0x15,
    ChannelNotOpened = 0x16,
    ChannelIdNotSet = 0x18,
    CloseAllChannels = 0x19,
    TransferInProgress = 0x1F,
    InvalidMessage = 0x28,
    InvalidNetworkNumber = 0x29,
}

/// <summary>Protocol-wide constant values.</summary>
public static class AntConstants
{
    /// <summary>TX/RX sync byte prefixing every frame.</summary>
    public const byte Sync = 0xA4;

    /// <summary>ANT+ RF frequency value (2457 MHz).</summary>
    public const byte AntPlusRfFrequency = 57;

    /// <summary>Wildcard search timeout (never time out).</summary>
    public const byte WildcardSearchTimeout = 0xFF;

    /// <summary>The synthetic "response to id" value used to mark an RF event in a 0x40 message.</summary>
    public const byte EventResponseMarker = 0x01;

    /// <summary>LibConfig flag bits.</summary>
    public const byte LibConfigRxTimestamp = 0x20;
    public const byte LibConfigRssi = 0x40;
    public const byte LibConfigChannelId = 0x80;
}
