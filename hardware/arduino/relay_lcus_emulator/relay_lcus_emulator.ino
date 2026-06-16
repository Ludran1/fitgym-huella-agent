/*
 * relay_lcus_emulator — Arduino Nano como puente USB-relé para HuellaAgent.
 *
 * Emula un módulo USB-relé LCUS-1: el agente manda por serial @9600 los mismos bytes
 * que usa el LCUS  ->  A0 01 01 A2 = ON   /   A0 01 00 A1 = OFF.
 * El Nano togglea D2, que va al pin TRIG del módulo relé (el de temporizador que ya tenés).
 * Asi el agente (UsbRelay.cs) funciona SIN cambios: solo apuntar RelayPort al COM del Nano.
 *
 * Cableado:
 *   Nano D2  -> TRIG del módulo relé
 *   Nano GND -> GND del módulo relé
 *   (el módulo relé sigue alimentado por su propio micro-USB de 5V)
 *
 * Modo del módulo timer: ponelo en el modo donde el relé sigue al TRIG (on mientras
 * TRIG activo). El agente hace el pulso (ON, espera ~700ms, OFF), asi el Nano controla
 * la duración. Si el relé queda INVERTIDO (cierra cuando deberia abrir), cambia
 * RELAY_ACTIVE_HIGH a false.
 */

const int  RELAY_PIN = 2;            // D2 -> TRIG del módulo
const bool RELAY_ACTIVE_HIGH = true; // true: HIGH = relé activado. Si invertido, poné false.

byte buf[4];
int  idx = 0;

void setRelay(bool on) {
  digitalWrite(RELAY_PIN, (on == RELAY_ACTIVE_HIGH) ? HIGH : LOW);
}

void setup() {
  Serial.begin(9600);
  pinMode(RELAY_PIN, OUTPUT);
  setRelay(false);                   // relé abierto al arrancar
}

void loop() {
  if (!Serial.available()) return;
  byte b = Serial.read();

  // Resync: todo comando empieza con 0xA0.
  if (idx == 0 && b != 0xA0) return;
  buf[idx++] = b;

  if (idx == 4) {
    // buf = A0 01 <01=on|00=off> <checksum>
    if (buf[1] == 0x01 && buf[2] == 0x01) setRelay(true);
    else if (buf[1] == 0x01 && buf[2] == 0x00) setRelay(false);
    idx = 0;
  }
}
