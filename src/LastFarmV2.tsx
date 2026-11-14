import React from "react";
import {
  AbsoluteFill,
  Audio,
  Easing,
  Freeze,
  Sequence,
  interpolate,
  spring,
  staticFile,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import { LastFarm } from "./LastFarm";

/**
 * LastFarmV2 = your original LastFarm (untouched) plus:
 *  1. sound: music bed (ducks under the narration) + sound effects timed to the action
 *  2. camera: slow push-ins per scene, opening punch-in, screen shake on the lock click and the growl
 *  3. finish: vignette + film grain, and a "PART 2" end card after the cliffhanger
 */

const FONT = "Impact, 'Arial Black', sans-serif";
const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

export const LAST_FARM_V2_FRAMES = 1590; // 1500 original + 90 end card

// ---------- audio ----------
// Narration windows (start frame, length in frames) — same starts as NARRATION in LastFarm.tsx.
const VOICE: [number, number][] = [
  [12, 85],
  [252, 70],
  [492, 56],
  [792, 123],
  [1152, 75],
];

const duck = (frame: number) => {
  let d = 0;
  for (const [s, len] of VOICE) {
    const a = interpolate(frame, [s - 6, s + 6], [0, 1], clamp);
    const b = interpolate(frame, [s + len - 4, s + len + 14], [1, 0], clamp);
    d = Math.max(d, Math.min(a, b));
  }
  return d;
};

const MUSIC_VOL = 0.5;
const musicVolume = (frame: number) =>
  MUSIC_VOL *
  (1 - 0.65 * duck(frame)) *
  interpolate(frame, [0, 20, LAST_FARM_V2_FRAMES - 45, LAST_FARM_V2_FRAMES], [0, 1, 1, 0], clamp);

// ---------- camera ----------
type Cam = { from: number; to: number; push: number; origin: string };
const CAMS: Cam[] = [
  { from: 0, to: 480, push: 0.08, origin: "38% 72%" }, // farmhouse table
  { from: 480, to: 780, push: 0.06, origin: "62% 74%" }, // gate
  { from: 780, to: 1140, push: 0.05, origin: "50% 45%" }, // map
  { from: 1140, to: 1500, push: 0.11, origin: "72% 74%" }, // night — tightens as the zombie closes in
];

const shake = (frame: number, start: number, len: number, amp: number) => {
  const t = frame - start;
  if (t < 0 || t > len) return [0, 0] as const;
  const k = amp * Math.pow(1 - t / len, 1.5);
  return [Math.sin(t * 7.3) * k, Math.cos(t * 9.1) * k] as const;
};

const Camera: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const frame = useCurrentFrame();
  const cam = CAMS.find((c) => frame >= c.from && frame < c.to) ?? CAMS[CAMS.length - 1];
  const p = interpolate(frame, [cam.from, cam.to], [0, 1], {
    ...clamp,
    easing: Easing.inOut(Easing.quad),
  });
  // opening punch-in that settles in the first ~0.6s
  const punch = interpolate(frame, [0, 18], [0.12, 0], { ...clamp, easing: Easing.out(Easing.cubic) });
  const scale = 1 + cam.push * p + punch;
  const [sx1, sy1] = shake(frame, 620, 14, 9); // lock clicks shut
  const [sx2, sy2] = shake(frame, 1230, 36, 7); // growl
  const [sx3, sy3] = shake(frame, 1498, 14, 10); // final sting
  return (
    <AbsoluteFill style={{ overflow: "hidden", background: "#000" }}>
      <AbsoluteFill
        style={{
          transformOrigin: cam.origin,
          transform: `translate(${sx1 + sx2 + sx3}px, ${sy1 + sy2 + sy3}px) scale(${scale})`,
        }}
      >
        {children}
      </AbsoluteFill>
    </AbsoluteFill>
  );
};

// ---------- finishing: vignette + grain ----------
const Finish: React.FC = () => {
  const frame = useCurrentFrame();
  return (
    <AbsoluteFill style={{ pointerEvents: "none" }}>
      <AbsoluteFill
        style={{
          background:
            "radial-gradient(ellipse at 50% 48%, rgba(0,0,0,0) 52%, rgba(0,0,0,0.55) 100%)",
        }}
      />
      <svg width="100%" height="100%" style={{ position: "absolute", inset: 0, mixBlendMode: "overlay", opacity: 0.09 }}>
        <filter id="grain">
          <feTurbulence type="fractalNoise" baseFrequency="0.85" numOctaves={2} seed={frame % 12} stitchTiles="stitch" />
          <feColorMatrix type="saturate" values="0" />
        </filter>
        <rect width="100%" height="100%" filter="url(#grain)" />
      </svg>
    </AbsoluteFill>
  );
};

// ---------- end card ----------
const EndCard: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const dim = interpolate(frame, [0, 14], [0, 0.72], clamp);
  const pop = (delay: number) =>
    spring({ frame: frame - delay, fps, config: { damping: 12, stiffness: 130 } });
  const p1 = pop(8);
  const p2 = pop(26);
  return (
    <AbsoluteFill style={{ alignItems: "center", justifyContent: "center" }}>
      <AbsoluteFill style={{ background: `rgba(4,4,14,${dim})` }} />
      <div style={{ textAlign: "center", fontFamily: FONT }}>
        <div
          style={{
            fontSize: 190,
            lineHeight: 1,
            color: "#fff",
            letterSpacing: 4,
            textShadow: "0 8px 0 #000, 0 0 40px rgba(224,87,46,0.6)",
            transform: `scale(${interpolate(p1, [0, 1], [0.4, 1])}) rotate(${interpolate(p1, [0, 1], [-5, 0])}deg)`,
            opacity: p1,
          }}
        >
          PART <span style={{ color: "#e0572e" }}>2</span>
        </div>
        <div
          style={{
            marginTop: 36,
            display: "inline-block",
            padding: "10px 34px",
            fontSize: 62,
            color: "#111114",
            background: "#ffc20e",
            transform: `scale(${interpolate(p2, [0, 1], [0.4, 1])}) rotate(-2deg)`,
            opacity: p2,
          }}
        >
          FOLLOW SO YOU DON'T MISS IT
        </div>
      </div>
    </AbsoluteFill>
  );
};

export const LastFarmV2: React.FC = () => (
  <AbsoluteFill style={{ background: "#000" }}>
    {/* picture */}
    <Sequence from={0} durationInFrames={1500}>
      <Camera>
        <LastFarm />
      </Camera>
    </Sequence>
    <Sequence from={1500} durationInFrames={90}>
      <Freeze frame={1499}>
        <Camera>
          <LastFarm />
        </Camera>
      </Freeze>
      <EndCard />
    </Sequence>
    <Finish />
    {/* sound — public/sfx/lf-music.mp3 and lf-sfx.mp3 */}
    <Audio src={staticFile("sfx/lf-music.mp3")} volume={musicVolume} />
    <Audio src={staticFile("sfx/lf-sfx.mp3")} volume={0.9} />
  </AbsoluteFill>
);
