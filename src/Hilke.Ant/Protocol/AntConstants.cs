namespace Hilke.Ant.Protocol;

/// <summary>ANT message IDs used by this library (ANT Message Protocol &amp; Usage, D00000652).</summary>
public enum AntMessageId : byte
{
    /// <summary>Unassign a previously assigned channel.</summary>
    UnassignChannel = 0x41,
    /// <summary>Assign a channel: type and network number.</summary>
    AssignChannel = 0x42,
    /// <summary>Set the channel's message period (1/32768 s counts).</summary>
    ChannelPeriod = 0x43,
    /// <summary>Set the channel's search timeout (0xFF = never time out).</summary>
    SearchTimeout = 0x44,
    /// <summary>Set the channel's RF frequency (2400 MHz + value MHz).</summary>
    RfFrequency = 0x45,
    /// <summary>Set the ANT network key for a network number.</summary>
    NetworkKey = 0x46,
    /// <summary>Set the radio's overall transmit power.</summary>
    TransmitPower = 0x47,
    /// <summary>Perform a full system reset.</summary>
    ResetSystem = 0x4A,
    /// <summary>Open a configured channel.</summary>
    OpenChannel = 0x4B,
    /// <summary>Close an open channel.</summary>
    CloseChannel = 0x4C,
    /// <summary>Request the device send a specific message (e.g. capabilities).</summary>
    RequestMessage = 0x4D,
    /// <summary>One-way broadcast data (channel 0 payload for scan, or a slave channel's periodic page).</summary>
    BroadcastData = 0x4E,
    /// <summary>Acknowledged (single, confirmed) data.</summary>
    AcknowledgedData = 0x4F,
    /// <summary>Multi-packet burst-transfer data.</summary>
    BurstData = 0x50,
    /// <summary>Set/report a channel's device id (device number, device type, transmission type).</summary>
    ChannelId = 0x51,
    /// <summary>Report a channel's current device-level status.</summary>
    ChannelStatus = 0x52,
    /// <summary>Report device capabilities (max channels/networks, options).</summary>
    Capabilities = 0x54,
    /// <summary>Channel response to a command, or an asynchronous RF event.</summary>
    ChannelResponseEvent = 0x40,
    /// <summary>Open channel 0 in continuous-scan (Rx scan) mode.</summary>
    OpenRxScanMode = 0x5B,
    /// <summary>Set a specific channel's transmit power, overriding the system default.</summary>
    ChannelTransmitPower = 0x60,
    /// <summary>Report the device's serial number.</summary>
    SerialNumber = 0x61,
    /// <summary>Enable/disable extended receive messages (appended channel id / RSSI / timestamp).</summary>
    EnableExtRxMessages = 0x66,
    /// <summary>Configure which extended fields (channel id, RSSI, timestamp) are appended to received data.</summary>
    LibConfig = 0x6E,
    /// <summary>Sent by the device after a reset, describing the reset reason.</summary>
    StartupMessage = 0x6F,
}

/// <summary>ANT channel type values (byte written in Assign Channel).</summary>
internal enum ChannelType : byte
{
    BidirectionalSlave = 0x00,
    BidirectionalMaster = 0x10,
    SharedSlave = 0x20,
    SharedMaster = 0x30,
    SlaveReceiveOnly = 0x40,
    MasterTransmitOnly = 0x50,
}

/// <summary>Device-reported channel state (low 2 bits of Channel Status).</summary>
internal enum DeviceChannelState : byte
{
    Unassigned = 0,
    Assigned = 1,
    Searching = 2,
    Tracking = 3,
}

/// <summary>Data payload classification for received data messages.</summary>
internal enum DataKind : byte
{
    Broadcast,
    Acknowledged,
    Burst,
}

/// <summary>Channel response codes and RF event codes (Channel Response / Event message 0x40).</summary>
public enum ChannelResponseCode : byte
{
    /// <summary>Command accepted with no error.</summary>
    ResponseNoError = 0x00,
    /// <summary>The channel's search timed out without finding a device.</summary>
    EventRxSearchTimeout = 0x01,
    /// <summary>An expected message was not received within a channel period.</summary>
    EventRxFail = 0x02,
    /// <summary>The device is ready for the next broadcast/acknowledged/burst payload.</summary>
    EventTx = 0x03,
    /// <summary>A receive burst transfer failed.</summary>
    EventTransferRxFailed = 0x04,
    /// <summary>An acknowledged or burst transmit completed successfully.</summary>
    EventTransferTxCompleted = 0x05,
    /// <summary>An acknowledged or burst transmit failed.</summary>
    EventTransferTxFailed = 0x06,
    /// <summary>The channel finished closing.</summary>
    EventChannelClosed = 0x07,
    /// <summary>Too many consecutive missed messages; the channel dropped back to searching.</summary>
    EventRxFailGoToSearch = 0x08,
    /// <summary>A shared-channel transmission collision was detected.</summary>
    EventChannelCollision = 0x09,
    /// <summary>A burst or acknowledged transmit has started transmitting.</summary>
    EventTransferTxStart = 0x0A,
    /// <summary>The command is invalid for the channel's current state.</summary>
    ChannelInWrongState = 0x15,
    /// <summary>The channel has not been opened.</summary>
    ChannelNotOpened = 0x16,
    /// <summary>The channel's id has not been set.</summary>
    ChannelIdNotSet = 0x18,
    /// <summary>Sent in response to a close-all-channels request.</summary>
    CloseAllChannels = 0x19,
    /// <summary>A transfer is already in progress on this channel.</summary>
    TransferInProgress = 0x1F,
    /// <summary>The message is malformed or not supported.</summary>
    InvalidMessage = 0x28,
    /// <summary>The specified network number is invalid.</summary>
    InvalidNetworkNumber = 0x29,
}

/// <summary>Protocol-wide constant values.</summary>
internal static class AntConstants
{
    /// <summary>TX/RX sync byte prefixing every frame.</summary>
    public const byte Sync = 0xA4;

    /// <summary>Wildcard search timeout (never time out).</summary>
    public const byte WildcardSearchTimeout = 0xFF;

    /// <summary>The synthetic "response to id" value used to mark an RF event in a 0x40 message.</summary>
    public const byte EventResponseMarker = 0x01;

    /// <summary>LibConfig flag bits.</summary>
    public const byte LibConfigRxTimestamp = 0x20;
    public const byte LibConfigRssi = 0x40;
    public const byte LibConfigChannelId = 0x80;
}
