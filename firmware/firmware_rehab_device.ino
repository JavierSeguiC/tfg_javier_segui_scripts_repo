// Force-sensor readout, per-finger calibration — index/middle/ring/pinky (A0..A3)
// Right hand: raw0=index, raw1=middle, raw2=ring, raw3=pinky
//
// DEBUG BUILD: RGB LED encodes WHICH phase of loop() is executing, so a freeze
// tells you exactly where it happened, not just that it happened.
//   BLUE (frozen)  -> hung inside analogRead() — ADC/sensor-side issue
//   RED  (frozen)  -> hung inside/around Serial.print() — USB/serial-side issue
//   GREEN (frozen) -> hung during delay(20) — freeze isn't in our code at all

const int NUM_LANES = 4;
const int lanePins[NUM_LANES] = {A0, A1, A2, A3};
const char* laneNames[NUM_LANES] = {"index", "middle", "ring", "pinky"};

const int ADC_RESOLUTION_BITS = 12;

// --- Per-finger calibration thresholds (raw ADC counts) ---
const float RAW_REST_INDEX  = 2400.0;
const float RAW_MAX_INDEX   = 2850.0;

const float RAW_REST_MIDDLE = 2400.0;
const float RAW_MAX_MIDDLE  = 2900.0;

const float RAW_REST_RING   = 2250.0;
const float RAW_MAX_RING    = 2850.0;

const float RAW_REST_PINKY  = 2350.0;
const float RAW_MAX_PINKY   = 2750.0;

const float RAW_REST[NUM_LANES] = {RAW_REST_INDEX, RAW_REST_MIDDLE, RAW_REST_RING, RAW_REST_PINKY};
const float RAW_MAX[NUM_LANES]  = {RAW_MAX_INDEX,  RAW_MAX_MIDDLE,  RAW_MAX_RING,  RAW_MAX_PINKY};

// Common-anode RGB LED: LOW = on, HIGH = off.
void setLED(bool r, bool g, bool b) {
  digitalWrite(LED_RED,   r ? LOW : HIGH);
  digitalWrite(LED_GREEN, g ? LOW : HIGH);
  digitalWrite(LED_BLUE,  b ? LOW : HIGH);
}

void setup() {
  Serial.begin(115200);
  Serial.setTxTimeoutMs(0);

  analogReadResolution(ADC_RESOLUTION_BITS);
  for (int i = 0; i < NUM_LANES; i++) pinMode(lanePins[i], INPUT);

  pinMode(LED_RED, OUTPUT);
  pinMode(LED_GREEN, OUTPUT);
  pinMode(LED_BLUE, OUTPUT);
  setLED(false, false, false);

  delay(500);
  Serial.println("raw_index,raw_middle,raw_ring,raw_pinky,norm_index,norm_middle,norm_ring,norm_pinky");
}

float normalizeForce(int raw, int laneIndex) {
  float norm = (raw - RAW_REST[laneIndex]) / (RAW_MAX[laneIndex] - RAW_REST[laneIndex]);
  return constrain(norm, 0.0, 1.0);
}

void loop() {
  int raw[NUM_LANES];
  float norm[NUM_LANES];

  // --- ADC phase: BLUE ---
  setLED(false, false, true);
  for (int i = 0; i < NUM_LANES; i++) {
    raw[i] = analogRead(lanePins[i]);
    norm[i] = normalizeForce(raw[i], i);
  }

  // --- Serial phase: RED ---
  setLED(true, false, false);

  // Belt-and-braces guard, on top of setTxTimeoutMs(0): don't even ATTEMPT the
  // write unless the TX buffer already has room for a full line. This is what
  // stops loop() from ever entering a risky Serial.print() call in the first
  // place, in case the earlier freeze was happening at a level setTxTimeoutMs
  // doesn't reach. Costs one skipped 20ms sample when it trips, never the sketch.
  const int ESTIMATED_LINE_BYTES = 64; // generous margin over the ~45 bytes/line this sketch sends

  if (Serial.availableForWrite() >= ESTIMATED_LINE_BYTES) {
    for (int i = 0; i < NUM_LANES; i++) {
      Serial.print(raw[i]);
      Serial.print(",");
    }
    for (int i = 0; i < NUM_LANES; i++) {
      Serial.print(norm[i], 2);
      if (i < NUM_LANES - 1) Serial.print(",");
    }
    Serial.println();
  }
  // else: skip this line entirely rather than risk a block.

  // --- idle/delay phase: GREEN ---
  setLED(false, true, false);
  delay(20); // ~50 Hz polling
}