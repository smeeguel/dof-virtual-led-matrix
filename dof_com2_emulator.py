import time
from datetime import datetime
import serial

PORT = "COM2"
BAUD = 9600
LOG_FILE = "dof_com2_log.txt"

MAX_LEDS_PER_CHANNEL = 1100


def ts():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]


def hex_bytes(data: bytes) -> str:
    return " ".join(f"{b:02X}" for b in data)


def ascii_safe(data: bytes) -> str:
    return "".join(chr(b) if 32 <= b <= 126 else "." for b in data)


def log_line(text: str):
    line = f"{ts()}  {text}"
    print(line)
    with open(LOG_FILE, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def send_reply(ser: serial.Serial, payload: bytes, reason: str):
    ser.write(payload)
    ser.flush()
    log_line(f"TX {len(payload):4d} bytes | HEX: {hex_bytes(payload)} | REASON: {reason}")


def main():
    log_line("==== Starting DOF COM2 emulator v5 ====")

    ser = serial.Serial(
        port=PORT,
        baudrate=BAUD,
        timeout=0.05,
        write_timeout=0.1,
        bytesize=serial.EIGHTBITS,
        parity=serial.PARITY_NONE,
        stopbits=serial.STOPBITS_ONE,
    )

    log_line(f"Opened {PORT} at {BAUD}")

    rx_buffer = bytearray()
    leds_per_channel = None
    last_frame = b""

    try:
        while True:
            incoming = ser.read(ser.in_waiting or 1)
            if incoming:
                rx_buffer.extend(incoming)
                log_line(
                    f"RX+ {len(incoming):4d} bytes | HEX: {hex_bytes(incoming[:64])}{' ...' if len(incoming) > 64 else ''} | ASCII: {ascii_safe(incoming[:64])}{'...' if len(incoming) > 64 else ''}"
                )
            else:
                time.sleep(0.002)

            made_progress = True
            while made_progress:
                made_progress = False

                if not rx_buffer:
                    continue

                cmd = rx_buffer[0]

                # 0x00 = command mode probe
                if cmd == 0x00:
                    del rx_buffer[0]
                    send_reply(ser, b"A", "ACK command-mode probe")
                    made_progress = True
                    continue

                # Ignore stray FB bytes if they show up from the bridge/path.
                if cmd == 0xFB:
                    del rx_buffer[0]
                    log_line("IGN    1 byte  | HEX: FB | REASON: Ignoring stray FB")
                    made_progress = True
                    continue

                # M -> reply with hi lo A
                if cmd == ord("M"):
                    del rx_buffer[0]
                    hi = (MAX_LEDS_PER_CHANNEL >> 8) & 0xFF
                    lo = MAX_LEDS_PER_CHANNEL & 0xFF
                    send_reply(ser, bytes([hi, lo, ord("A")]), "Reply to M (max LEDs/channel)")
                    made_progress = True
                    continue

                # L + 2 bytes
                if cmd == ord("L"):
                    if len(rx_buffer) < 3:
                        continue
                    packet = bytes(rx_buffer[:3])
                    del rx_buffer[:3]

                    leds_per_channel = (packet[1] << 8) | packet[2]
                    log_line(
                        f"CMD L | HEX: {hex_bytes(packet)} | Parsed leds_per_channel={leds_per_channel}"
                    )
                    send_reply(ser, b"A", "ACK L (set LEDs/channel)")
                    made_progress = True
                    continue

                # C
                if cmd == ord("C"):
                    del rx_buffer[0]
                    last_frame = b""
                    send_reply(ser, b"A", "ACK C (clear buffer)")
                    made_progress = True
                    continue

                # O
                if cmd == ord("O"):
                    del rx_buffer[0]
                    send_reply(ser, b"A", "ACK O (output buffer)")
                    made_progress = True
                    continue

                # R + target_hi + target_lo + count_hi + count_lo + rgb payload
                if cmd == ord("R"):
                    if len(rx_buffer) < 5:
                        continue

                    target_position = (rx_buffer[1] << 8) | rx_buffer[2]
                    nr_of_leds = (rx_buffer[3] << 8) | rx_buffer[4]
                    payload_len = nr_of_leds * 3
                    total_len = 5 + payload_len

                    if len(rx_buffer) < total_len:
                        continue

                    packet = bytes(rx_buffer[:total_len])
                    del rx_buffer[:total_len]

                    rgb = packet[5:]
                    last_frame = rgb

                    preview = rgb[:48]
                    log_line(
                        f"CMD R | target_position={target_position}, nr_of_leds={nr_of_leds}, payload_len={payload_len}"
                    )
                    log_line(
                        f"RGB {payload_len:4d} bytes | HEX(first 48): {hex_bytes(preview)}{' ...' if len(rgb) > 48 else ''}"
                    )

                    send_reply(ser, b"A", f"ACK R (strip data for {nr_of_leds} LEDs)")
                    made_progress = True
                    continue

                # Unknown byte: discard and keep going
                unknown = rx_buffer[0]
                del rx_buffer[0]
                log_line(f"Unhandled byte dropped: 0x{unknown:02X}")
                made_progress = True

    except KeyboardInterrupt:
        log_line("Stopped by user.")
    except serial.SerialException as e:
        log_line(f"Serial error: {e}")
    except Exception as e:
        log_line(f"Unexpected error: {e}")
        raise
    finally:
        ser.close()
        log_line(f"Closed {PORT}")
        log_line("==== Emulator stopped ====")


if __name__ == "__main__":
    main()