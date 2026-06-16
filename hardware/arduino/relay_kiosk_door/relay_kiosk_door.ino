/*
 * relay_kiosk_door — Arduino Nano como "puerta electrónica" del kiosko FitGym.
 *
 * El kiosko ya tiene puerta por Web Serial (boton "Conectar puerta"): al conceder acceso
 * manda 'O' (abrir) por serial @9600, espera ~800ms y manda 'C' (cerrar). Este sketch hace
 * que el Nano togglee D2 -> TRIG del modulo rele -> COM/NO -> entrada de apertura del
 * torniquete. NO hace falta el agente para el torniquete: el navegador habla directo al Nano.
 *
 * Cableado:
 *   Nano D2  -> TRIG del modulo rele
 *   Nano GND -> GND del modulo rele
 *   Rele COM + NO -> entrada de "apertura/release" del torniquete (contacto seco momentaneo)
 *   (el modulo rele sigue alimentado por su micro-USB de 5V; el Nano por su USB a la PC)
 *
 * Uso: subir este sketch, enchufar el Nano a la PC del kiosko, click "Conectar puerta" en el
 * kiosko y elegir el puerto del Nano. Listo: cada acceso concedido abre el torniquete ~800ms.
 *
 * Modo del modulo timer: ponelo en el modo donde el rele sigue al TRIG (on mientras TRIG
 * activo). Si el rele queda INVERTIDO, cambia RELAY_ACTIVE_HIGH a false.
 */

const int  RELAY_PIN = 2;            // D2 -> TRIG del modulo
const bool RELAY_ACTIVE_HIGH = true; // si el rele queda invertido, poné false

void setRelay(bool on) {
  digitalWrite(RELAY_PIN, (on == RELAY_ACTIVE_HIGH) ? HIGH : LOW);
}

void setup() {
  Serial.begin(9600);                // mismo baudrate que el kiosko (port.open baudRate 9600)
  pinMode(RELAY_PIN, OUTPUT);
  setRelay(false);                   // torniquete cerrado al arrancar
}

void loop() {
  if (!Serial.available()) return;
  char c = Serial.read();
  if (c == 'O') setRelay(true);      // abrir
  else if (c == 'C') setRelay(false); // cerrar
}
