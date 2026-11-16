// A small C interface to CoreBluetooth, for the iPad build's Square link (Apple/AppleSquareLink.cs calls it) and its
// Garmin R10 link (Apple/AppleR10Link.cs: the "all characteristics" calls at the end).
// One monitor at a time. Everything runs on its own serial queue, and the two callbacks below are called on it.
// Built into libggble.a by tools/ios-native.sh and linked into the app by tools/ios-export.sh.
#ifndef GG_BLE_H
#define GG_BLE_H

#include <stdint.h>

/// A monitor was heard advertising. The address is made from the first six bytes of the device's identifier:
/// iOS never shows a Bluetooth address, and this stays the same for a device from one run to the next.
/// manufacturer is the manufacturer-specific data of this advertisement (its first two bytes are the company
/// identifier, little-endian), or NULL / 0 if it had none: a Garmin R10 is recognised by it.
typedef void (*gg_ble_advert_fn)(uint64_t address, const char *name, int rssi, const uint8_t *manufacturer, int manufacturer_length);

/// Something happened. kind is one of the GG_ values below; token is the one given to gg_ble_read, gg_ble_subscribe
/// or gg_ble_write_to (for what answers those, and for a subscription's notifications); data (length bytes) is text
/// for the problems and the bytes for notifications and reads.
typedef void (*gg_ble_event_fn)(int kind, int token, uint64_t address, const uint8_t *data, int length);

enum {
    GG_SCAN_STOPPED = 1,    // the scan can't go on (Bluetooth off or not allowed); data is why
    GG_CONNECTED = 2,       // connected, with the characteristics found and notifications on (after gg_ble_connect_all:
                            // data is a list, one line per service ("S <uuid>") and characteristic ("C <uuid> <properties in hex>"))
    GG_CONNECT_FAILED = 3,  // data is why
    GG_DISCONNECTED = 4,
    GG_NOTIFICATION = 5,    // data is what the monitor sent (after gg_ble_subscribe: token says which characteristic)
    GG_READ_DONE = 6,       // data is the value
    GG_READ_FAILED = 7,     // data is why
    GG_LOG = 8,             // data is a line for the log
    GG_SUBSCRIBED = 9,      // gg_ble_subscribe worked (token)
    GG_SUBSCRIBE_FAILED = 10,   // data is why (token)
    GG_WRITE_DONE = 11,     // gg_ble_write_to went out (token)
    GG_WRITE_FAILED = 12    // data is why (token)
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

// ---- For a monitor with several services and characteristics (the Garmin R10), which the caller picks from afterwards.
// gg_ble_read works on any characteristic found.

/// Connects to the monitor with this address (scanning for it first unless it has been heard this run), and finds
/// every service and characteristic it has. Ends in GG_CONNECTED (with the list) or GG_CONNECT_FAILED, which says
/// not_found if the monitor wasn't heard within timeout_seconds. Nothing is subscribed to yet.
void gg_ble_connect_all(uint64_t address, int timeout_seconds, const char *not_found);

/// Turns on notifications for a characteristic of the connection. Ends in GG_SUBSCRIBED or GG_SUBSCRIBE_FAILED with
/// this token, and the notifications that follow are GG_NOTIFICATION with it. If the characteristic needs the monitor to
/// be bonded, iOS asks the user to pair it here, the first time, and the answer comes once they have.
void gg_ble_subscribe(const char *characteristic, int token);

/// Writes to a characteristic, with a response if it has one (else without), after the writes already waiting.
/// Ends in GG_WRITE_DONE or GG_WRITE_FAILED with this token.
void gg_ble_write_to(const char *characteristic, const uint8_t *data, int length, int token);

#endif
