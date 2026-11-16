// A small C interface to CoreBluetooth, for the iPad build's Square link (Apple/AppleSquareLink.cs calls it).
// One monitor at a time. Everything runs on its own serial queue, and the two callbacks below are called on it.
// Built into libggble.a by tools/ios-native.sh and linked into the app by tools/ios-export.sh.
#ifndef GG_BLE_H
#define GG_BLE_H

#include <stdint.h>

/// A monitor was heard advertising. The address is made from the first six bytes of the device's identifier:
/// iOS never shows a Bluetooth address, and this stays the same for a device from one run to the next.
typedef void (*gg_ble_advert_fn)(uint64_t address, const char *name, int rssi);

/// Something happened. kind is one of the GG_ values below; token is the one given to gg_ble_read, for the read
/// results; data (length bytes) is text for the problems and the bytes for notifications and reads.
typedef void (*gg_ble_event_fn)(int kind, int token, uint64_t address, const uint8_t *data, int length);

enum {
    GG_SCAN_STOPPED = 1,    // the scan can't go on (Bluetooth off or not allowed); data is why
    GG_CONNECTED = 2,       // connected, with the characteristics found and notifications on
    GG_CONNECT_FAILED = 3,  // data is why
    GG_DISCONNECTED = 4,
    GG_NOTIFICATION = 5,    // data is what the monitor sent
    GG_READ_DONE = 6,       // data is the value
    GG_READ_FAILED = 7,     // data is why
    GG_LOG = 8              // data is a line for the log
};

void gg_ble_set_callbacks(gg_ble_advert_fn advert, gg_ble_event_fn event);

/// Starts listening for advertisements (asks the user's permission the first time). Safe to call while scanning.
void gg_ble_start_scan(void);
void gg_ble_stop_scan(void);

/// Connects to the monitor with this address: scans for it unless it has already been heard this run, then finds
/// the service and its characteristics and subscribes to the notifying one. Ends in GG_CONNECTED or GG_CONNECT_FAILED.
/// The UUIDs are text, like "86601001-6b7e-439a-bdd1-489a3213e9bb".
void gg_ble_connect(uint64_t address, const char *service, const char *command, const char *notify, int timeout_seconds);
void gg_ble_disconnect(void);

/// Writes to the command characteristic, without a response. Returns 0 if there's no connection to write to.
/// (A write is queued for the Bluetooth queue, so it's in order with the rest.)
int gg_ble_write(const uint8_t *data, int length);

/// Reads a characteristic of the connected monitor; the answer is a GG_READ_DONE or GG_READ_FAILED with this token.
void gg_ble_read(const char *characteristic, int token);

#endif
