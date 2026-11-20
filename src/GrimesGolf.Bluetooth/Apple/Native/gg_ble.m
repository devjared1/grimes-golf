// CoreBluetooth side of the iPad build's Square link: see gg_ble.h. Compiled with ARC.
#import <CoreBluetooth/CoreBluetooth.h>
#import <Foundation/Foundation.h>
#include "gg_ble.h"

static gg_ble_advert_fn g_advert = NULL;
static gg_ble_event_fn g_event = NULL;

static uint64_t AddressOf(CBPeripheral *peripheral) {
    uuid_t bytes;
    [peripheral.identifier getUUIDBytes:bytes];
    uint64_t address = 0;
    for (int i = 0; i < 6; i++) address = (address << 8) | bytes[i];
    return address;
}

@interface GgBle : NSObject <CBCentralManagerDelegate, CBPeripheralDelegate>
@end

@implementation GgBle {
    dispatch_queue_t _queue;
    CBCentralManager *_central;
    NSMutableDictionary<NSNumber *, CBPeripheral *> *_known;   // heard this run, by address

    BOOL _reportAdverts;      // a scan was asked for
    BOOL _findTarget;         // a connection is waiting to hear its monitor
    BOOL _scanning;

    uint64_t _target;
    CBUUID *_serviceUUID, *_commandUUID, *_notifyUUID;
    CBPeripheral *_peripheral;
    CBCharacteristic *_command, *_notify;
    NSMutableDictionary<NSString *, CBCharacteristic *> *_characteristics;
    NSMutableDictionary<NSString *, NSNumber *> *_reads;       // characteristic UUID -> token
    BOOL _ready;
    int _attempt;             // told apart from an earlier connection's timeout

    NSMutableArray<NSData *> *_outbox;   // commands waiting to go out, in order
    BOOL _writeWithResponse;  // the command characteristic has no write-without-response
    BOOL _writeInFlight;      // a with-response write is waiting for its answer
    int _written, _notifications;        // this connection, for the log
}

+ (GgBle *)shared {
    static GgBle *instance;
    static dispatch_once_t once;
    dispatch_once(&once, ^{ instance = [[GgBle alloc] init]; });
    return instance;
}

- (instancetype)init {
    if ((self = [super init])) {
        _queue = dispatch_queue_create("grimesgolf.ble", DISPATCH_QUEUE_SERIAL);
        _known = [NSMutableDictionary dictionary];
        _characteristics = [NSMutableDictionary dictionary];
        _reads = [NSMutableDictionary dictionary];
        _outbox = [NSMutableArray array];
    }
    return self;
}

- (void)run:(dispatch_block_t)block { dispatch_async(_queue, block); }

// ---- Events out

- (void)emit:(int)kind token:(int)token address:(uint64_t)address data:(const uint8_t *)data length:(int)length {
    if (g_event) g_event(kind, token, address, data, length);
}

- (void)emitText:(int)kind token:(int)token text:(NSString *)text {
    const char *utf8 = text.UTF8String;
    [self emit:kind token:token address:_target data:(const uint8_t *)utf8 length:(int)strlen(utf8)];
}

/// A line for the app's log.
- (void)log:(NSString *)text { [self emitText:GG_LOG token:0 text:text]; }

// ---- Scanning

/// Creates the central manager the first time (that's when iOS asks for permission) and starts scanning once it's on.
- (void)ensureScanning {
    if (!_central) {
        _central = [[CBCentralManager alloc] initWithDelegate:self queue:_queue options:nil];
        return;   // centralManagerDidUpdateState carries on
    }
    if (_central.state != CBManagerStatePoweredOn || _scanning) return;
    _scanning = YES;
    [_central scanForPeripheralsWithServices:nil options:@{ CBCentralManagerScanOptionAllowDuplicatesKey: @YES }];
}

- (void)stopScanningIfUnwanted {
    if (_reportAdverts || _findTarget || !_scanning) return;
    [_central stopScan];
    _scanning = NO;
}

- (void)startScan { _reportAdverts = YES; [self ensureScanning]; }
- (void)stopScan { _reportAdverts = NO; [self stopScanningIfUnwanted]; }

- (NSString *)whyNotOn {
    switch (_central.state) {
        case CBManagerStateUnauthorized: return @"Bluetooth isn't allowed for Grimes Golf. Allow it in Settings > Grimes Golf > Bluetooth.";
        case CBManagerStatePoweredOff: return @"Bluetooth is turned off. Turn it on in Settings or Control Center.";
        case CBManagerStateUnsupported: return @"This device has no Bluetooth LE.";
        default: return @"Bluetooth isn't ready.";
    }
}

- (void)centralManagerDidUpdateState:(CBCentralManager *)central {
    if (central.state == CBManagerStatePoweredOn) {
        if (_reportAdverts || _findTarget) [self ensureScanning];
        return;
    }
    _scanning = NO;
    NSString *why = [self whyNotOn];
    if (_reportAdverts) [self emitText:GG_SCAN_STOPPED token:0 text:why];
    _reportAdverts = NO;
    if (_findTarget) { _findTarget = NO; _attempt++; [self emitText:GG_CONNECT_FAILED token:0 text:why]; }
    if (_peripheral) [self closeConnection];
}

- (void)centralManager:(CBCentralManager *)central didDiscoverPeripheral:(CBPeripheral *)peripheral
     advertisementData:(NSDictionary<NSString *, id> *)advertisementData RSSI:(NSNumber *)RSSI {
    uint64_t address = AddressOf(peripheral);
    _known[@(address)] = peripheral;
    if (_reportAdverts && g_advert) {
        NSString *name = advertisementData[CBAdvertisementDataLocalNameKey] ?: peripheral.name ?: @"";
        g_advert(address, name.UTF8String, RSSI.intValue);
    }
    if (_findTarget && address == _target) {
        _findTarget = NO;
        [self stopScanningIfUnwanted];
        [self open:peripheral];
    }
}

// ---- Connecting

- (void)connect:(uint64_t)address service:(NSString *)service command:(NSString *)command notify:(NSString *)notify timeout:(int)seconds {
    if (_peripheral) [self closeConnection];
    _target = address;
    _serviceUUID = [CBUUID UUIDWithString:service];
    _commandUUID = [CBUUID UUIDWithString:command];
    _notifyUUID = [CBUUID UUIDWithString:notify];
    _ready = NO;
    _written = 0; _notifications = 0;
    int attempt = ++_attempt;

    // Give up if it hasn't worked in time (a monitor that's off, or out of range, or already in another app's hands).
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, seconds * NSEC_PER_SEC), _queue, ^{
        if (attempt != self->_attempt || self->_ready) return;
        self->_findTarget = NO;
        [self stopScanningIfUnwanted];
        if (self->_peripheral) [self closeConnection];
        [self emitText:GG_CONNECT_FAILED token:0 text:@"No Square launch monitor found. Is it on, and not connected to the phone app?"];
    });

    CBPeripheral *known = _known[@(address)];
    if (known) { [self open:known]; return; }
    _findTarget = YES;
    [self ensureScanning];
}

- (void)open:(CBPeripheral *)peripheral {
    _peripheral = peripheral;
    peripheral.delegate = self;
    [_central connectPeripheral:peripheral options:nil];
}

- (void)closeConnection {
    CBPeripheral *peripheral = _peripheral;
    _peripheral = nil; _command = nil; _notify = nil; _ready = NO;
    [_outbox removeAllObjects];
    _writeInFlight = NO;
    [_characteristics removeAllObjects];
    [_reads removeAllObjects];
    if (peripheral) [_central cancelPeripheralConnection:peripheral];
}

- (void)failConnection:(NSString *)why {
    _attempt++;
    [self closeConnection];
    [self emitText:GG_CONNECT_FAILED token:0 text:why];
}

- (void)centralManager:(CBCentralManager *)central didConnectPeripheral:(CBPeripheral *)peripheral {
    [peripheral discoverServices:@[_serviceUUID]];
}

- (void)centralManager:(CBCentralManager *)central didFailToConnectPeripheral:(CBPeripheral *)peripheral error:(NSError *)error {
    if (peripheral != _peripheral) return;
    [self failConnection:[NSString stringWithFormat:@"Couldn't connect: %@", error.localizedDescription ?: @"unknown error"]];
}

- (void)centralManager:(CBCentralManager *)central didDisconnectPeripheral:(CBPeripheral *)peripheral error:(NSError *)error {
    if (peripheral != _peripheral) return;
    BOOL wasReady = _ready;
    // Why iOS says the link ended: a timeout, or the monitor hanging up, read very differently.
    [self log:[NSString stringWithFormat:@"iOS reports the link ended (%@ %ld: %@); %d commands sent, %d notifications heard, %d unsent",
               error.domain ?: @"no error", (long)error.code, error.localizedDescription ?: @"none",
               _written, _notifications, (int)_outbox.count]];
    if (!wasReady) { [self failConnection:@"The monitor disconnected while connecting."]; return; }
    [self closeConnection];
    [self emit:GG_DISCONNECTED token:0 address:_target data:NULL length:0];
}

- (void)peripheral:(CBPeripheral *)peripheral didDiscoverServices:(NSError *)error {
    if (peripheral != _peripheral) return;
    CBService *service = peripheral.services.firstObject;
    if (error || !service) { [self failConnection:@"The Square's Bluetooth service wasn't found."]; return; }
    [peripheral discoverCharacteristics:nil forService:service];
}

- (void)peripheral:(CBPeripheral *)peripheral didDiscoverCharacteristicsForService:(CBService *)service error:(NSError *)error {
    if (peripheral != _peripheral) return;
    for (CBCharacteristic *c in service.characteristics) {
        _characteristics[c.UUID.UUIDString.uppercaseString] = c;
        if ([c.UUID isEqual:_commandUUID]) _command = c;
        if ([c.UUID isEqual:_notifyUUID]) _notify = c;
    }
    if (error || !_command || !_notify) { [self failConnection:@"The Square's characteristics weren't found."]; return; }
    // Write without response is what the official app uses; if this monitor doesn't offer it, write with a response
    // (asking CoreBluetooth for a type the characteristic lacks gets the write thrown away).
    _writeWithResponse = !(_command.properties & CBCharacteristicPropertyWriteWithoutResponse);
    [self log:[NSString stringWithFormat:@"Command characteristic properties 0x%lX (%@), notify characteristic properties 0x%lX, MTU %lu",
               (unsigned long)_command.properties, _writeWithResponse ? @"writing with a response" : @"writing without a response",
               (unsigned long)_notify.properties,
               (unsigned long)[peripheral maximumWriteValueLengthForType:CBCharacteristicWriteWithoutResponse]]];
    [peripheral setNotifyValue:YES forCharacteristic:_notify];
}

- (void)peripheral:(CBPeripheral *)peripheral didUpdateNotificationStateForCharacteristic:(CBCharacteristic *)characteristic error:(NSError *)error {
    if (peripheral != _peripheral || _ready) return;
    if (error || !characteristic.isNotifying) {
        [self failConnection:[NSString stringWithFormat:@"Couldn't subscribe to the Square's notifications: %@", error.localizedDescription ?: @"refused"]];
        return;
    }
    _ready = YES;
    [self emit:GG_CONNECTED token:0 address:_target data:NULL length:0];
}

// ---- Data

- (void)peripheral:(CBPeripheral *)peripheral didUpdateValueForCharacteristic:(CBCharacteristic *)characteristic error:(NSError *)error {
    if (peripheral != _peripheral) return;
    NSString *key = characteristic.UUID.UUIDString.uppercaseString;
    NSNumber *token = _reads[key];
    if (token) {   // the answer to a read, not a notification
        [_reads removeObjectForKey:key];
        if (error || !characteristic.value) {
            [self emitText:GG_READ_FAILED token:token.intValue text:error.localizedDescription ?: @"no value"];
        } else {
            NSData *value = characteristic.value;
            [self emit:GG_READ_DONE token:token.intValue address:_target data:value.bytes length:(int)value.length];
        }
        return;
    }
    if (error || characteristic != _notify || !characteristic.value) {
        if (error) [self log:[NSString stringWithFormat:@"Update error on %@: %@", key, error.localizedDescription]];
        return;
    }
    NSData *value = characteristic.value;
    if (_notifications++ == 0) [self log:[NSString stringWithFormat:@"First notification: %lu bytes", (unsigned long)value.length]];
    [self emit:GG_NOTIFICATION token:0 address:_target data:value.bytes length:(int)value.length];
}

/// Whether a write could go now. Read from any thread, so it can be a moment out of date: harmless, as a write
/// that finds no connection is dropped.
- (BOOL)canWrite { return _ready; }

- (void)write:(NSData *)data {
    if (!_peripheral || !_command || !_ready) { [self log:@"A command was dropped: no connection to send it on"]; return; }
    [_outbox addObject:data];
    [self pump];
}

/// Sends what's waiting, one at a time as CoreBluetooth allows: it throws away write-without-response data
/// it has no room for, so a burst of commands has to wait for peripheralIsReadyToSendWriteWithoutResponse.
- (void)pump {
    while (_outbox.count > 0 && _peripheral && _command && _ready) {
        if (_writeWithResponse) {
            if (_writeInFlight) return;
            _writeInFlight = YES;
        } else if (!_peripheral.canSendWriteWithoutResponse) {
            return;
        }
        NSData *data = _outbox.firstObject;
        [_outbox removeObjectAtIndex:0];
        [_peripheral writeValue:data forCharacteristic:_command
                           type:_writeWithResponse ? CBCharacteristicWriteWithResponse : CBCharacteristicWriteWithoutResponse];
        _written++;
    }
}

- (void)peripheralIsReadyToSendWriteWithoutResponse:(CBPeripheral *)peripheral {
    if (peripheral == _peripheral) [self pump];
}

- (void)peripheral:(CBPeripheral *)peripheral didWriteValueForCharacteristic:(CBCharacteristic *)characteristic error:(NSError *)error {
    if (peripheral != _peripheral) return;
    _writeInFlight = NO;
    if (error) [self log:[NSString stringWithFormat:@"A command write failed: %@", error.localizedDescription]];
    [self pump];
}

- (void)read:(NSString *)uuid token:(int)token {
    CBCharacteristic *c = _characteristics[uuid.uppercaseString];
    if (!_peripheral || !c) { [self emitText:GG_READ_FAILED token:token text:@"not connected, or no such characteristic"]; return; }
    _reads[uuid.uppercaseString] = @(token);
    [_peripheral readValueForCharacteristic:c];
}

- (void)disconnect {
    _attempt++;
    _findTarget = NO;
    [self stopScanningIfUnwanted];
    if (_peripheral) [self closeConnection];
}

@end

// ---- The C interface

void gg_ble_set_callbacks(gg_ble_advert_fn advert, gg_ble_event_fn event) {
    g_advert = advert;
    g_event = event;
}

void gg_ble_start_scan(void) { [[GgBle shared] run:^{ [[GgBle shared] startScan]; }]; }
void gg_ble_stop_scan(void) { [[GgBle shared] run:^{ [[GgBle shared] stopScan]; }]; }

void gg_ble_connect(uint64_t address, const char *service, const char *command, const char *notify, int timeout_seconds) {
    NSString *s = @(service), *c = @(command), *n = @(notify);
    [[GgBle shared] run:^{ [[GgBle shared] connect:address service:s command:c notify:n timeout:timeout_seconds]; }];
}

void gg_ble_disconnect(void) { [[GgBle shared] run:^{ [[GgBle shared] disconnect]; }]; }

int gg_ble_write(const uint8_t *data, int length) {
    if (![[GgBle shared] canWrite]) return 0;
    NSData *copy = [NSData dataWithBytes:data length:length];
    [[GgBle shared] run:^{ [[GgBle shared] write:copy]; }];
    return 1;
}

void gg_ble_read(const char *characteristic, int token) {
    NSString *uuid = @(characteristic);
    [[GgBle shared] run:^{ [[GgBle shared] read:uuid token:token]; }];
}
