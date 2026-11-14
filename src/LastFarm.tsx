import React from "react";
import {
  AbsoluteFill,
  Audio,
  Easing,
  staticFile,
  interpolate,
  Sequence,
  spring,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";

const W = 1080;
const H = 1920;
const GROUND = 1450;
const INK = "#111114";
const FONT = "Impact, 'Arial Black', sans-serif";

const rad = (d: number) => (d * Math.PI) / 180;
const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

const useSpring = (delay: number, damping = 14, stiffness = 120) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  return spring({ frame: frame - delay, fps, config: { damping, stiffness } });
};

// ---------- Stick figure ----------
type Pose = {
  lean: number;
  lThigh: number;
  lKnee: number;
  rThigh: number;
  rKnee: number;
  lArm: number;
  lElbow: number;
  rArm: number;
  rElbow: number;
};

const STAND: Pose = {
  lean: 0,
  lThigh: 3,
  lKnee: 0,
  rThigh: -3,
  rKnee: 0,
  lArm: 6,
  lElbow: 8,
  rArm: -4,
  rElbow: 8,
};
const SIT: Pose = {
  lean: 6,
  lThigh: 90,
  lKnee: 90,
  rThigh: 90,
  rKnee: 90,
  lArm: 55,
  lElbow: 40,
  rArm: 65,
  rElbow: 30,
};
const mixPose = (a: Pose, b: Pose, t: number): Pose => {
  const out = {} as Pose;
  (Object.keys(a) as (keyof Pose)[]).forEach((k) => {
    out[k] = a[k] + (b[k] - a[k]) * t;
  });
  return out;
};
const runPose = (phase: number): Pose => {
  const s = Math.sin(phase);
  const c = Math.cos(phase);
  return {
    lean: 16,
    lThigh: 50 * s,
    lKnee: 35 + 45 * Math.max(0, -c),
    rThigh: -50 * s,
    rKnee: 35 + 45 * Math.max(0, c),
    lArm: -55 * s,
    lElbow: 80,
    rArm: 55 * s,
    rElbow: 80,
  };
};
const zombiePose = (phase: number): Pose => {
  const s = Math.sin(phase);
  return {
    lean: 10,
    lThigh: 22 * s,
    lKnee: 6 + 12 * Math.max(0, -s),
    rThigh: -22 * s,
    rKnee: 6 + 12 * Math.max(0, s),
    lArm: 82,
    lElbow: 4,
    rArm: 86,
    rElbow: 4,
  };
};

const Figure: React.FC<{
  x: number;
  hipY: number;
  pose: Pose;
  dir?: 1 | -1;
  scale?: number;
  color?: string;
  halo?: boolean;
  sw?: number;
  head?: number;
}> = ({ x, hipY, pose, dir = 1, scale = 1, color = INK, halo = true, sw = 11, head = 38 }) => {
  const TORSO = 130;
  const UA = 70;
  const FA = 66;
  const TH = 90;
  const SH = 90;
  const pt = (ox: number, oy: number, len: number, a: number): [number, number] => [
    ox + dir * len * Math.sin(rad(a)),
    oy + len * Math.cos(rad(a)),
  ];
  const hip: [number, number] = [0, 0];
  const sh: [number, number] = [
    dir * TORSO * Math.sin(rad(pose.lean)),
    -TORSO * Math.cos(rad(pose.lean)),
  ];
  const headC: [number, number] = [
    sh[0] + dir * (head + 8) * Math.sin(rad(pose.lean)),
    sh[1] - (head + 8) * Math.cos(rad(pose.lean)),
  ];
  const limb = (
    from: [number, number],
    l1: number,
    a1: number,
    l2: number,
    a2: number,
  ) => {
    const m = pt(from[0], from[1], l1, a1);
    const e = pt(m[0], m[1], l2, a2);
    return `M${from[0]} ${from[1]} L${m[0]} ${m[1]} L${e[0]} ${e[1]}`;
  };
  const legPath = (th: number, kn: number) => {
    const knee = pt(0, 0, TH, th);
    const foot = pt(knee[0], knee[1], SH, th - kn);
    return `${limb(hip, TH, th, SH, th - kn)} L${foot[0] + dir * 28} ${foot[1]}`;
  };
  const body = [
    `M${hip[0]} ${hip[1]} L${sh[0]} ${sh[1]}`,
    legPath(pose.lThigh, pose.lKnee),
    legPath(pose.rThigh, pose.rKnee),
    limb(sh, UA, pose.lArm, FA, pose.lArm + pose.lElbow),
    limb(sh, UA, pose.rArm, FA, pose.rArm + pose.rElbow),
  ].join(" ");
  return (
    <g transform={`translate(${x} ${hipY}) scale(${scale})`}>
      {halo && (
        <g
          stroke="#ffffff"
          strokeOpacity={0.4}
          strokeWidth={sw + 8}
          strokeLinecap="round"
          strokeLinejoin="round"
          fill="none"
        >
          <path d={body} />
          <circle cx={headC[0]} cy={headC[1]} r={head} />
        </g>
      )}
      <g
        stroke={color}
        strokeWidth={sw}
        strokeLinecap="round"
        strokeLinejoin="round"
        fill="none"
      >
        <path d={body} />
        <circle
          cx={headC[0]}
          cy={headC[1]}
          r={head}
          fill={color === INK ? "#f4efe4" : color}
        />
      </g>
      {color === INK && (
        <circle
          cx={headC[0] + dir * 16}
          cy={headC[1] - 4}
          r={5}
          fill={INK}
        />
      )}
    </g>
  );
};

// ---------- Dog ----------
const Dog: React.FC<{
  x: number;
  dir?: 1 | -1;
  headUp?: number; // 0 = level, 1 = looking up at the distance
  wag?: number;
  growl?: number; // 0..1
  crouch?: number; // 0..1
  t?: number;
}> = ({ x, dir = 1, headUp = 0, wag = 0, growl = 0, crouch = 0, t = 0 }) => {
  const shake = growl * Math.sin(t * 2.4) * 3;
  const drop = crouch * 14;
  const fill = "#c98b4e";
  return (
    <g transform={`translate(${x + shake} ${GROUND}) scale(${dir} 1)`}>
      <g stroke="#fff" strokeOpacity={0.4} strokeWidth={19} strokeLinecap="round" fill="none">
        <path d={`M-48 ${-60 + drop} L-56 0 M-26 ${-60 + drop} L-22 0 M30 ${-60 + drop} L32 0 M54 ${-60 + drop} L66 0`} />
      </g>
      <g stroke={INK} strokeWidth={11} strokeLinecap="round" strokeLinejoin="round" fill="none">
        {/* legs */}
        <path d={`M-48 ${-60 + drop} L-56 0 M-26 ${-60 + drop} L-22 0 M30 ${-60 + drop} L32 0 M54 ${-60 + drop} L66 0`} />
        {/* tail */}
        <path d={`M-78 ${-92 + drop} Q${-112 + wag * 14} ${-110 + drop} ${-112 + wag * 20} ${-150 + drop}`} />
        {/* body */}
        <rect x={-82} y={-110 + drop} width={166} height={62} rx={30} fill={fill} />
        {/* head */}
        <g transform={`translate(78 ${-100 + drop + growl * 16}) rotate(${-headUp * 28})`}>
          <circle cx={26} cy={-24} r={36} fill={fill} />
          <path d={`M60 ${-16} L${86} ${-10}`} />
          <path d="M6 -52 L-4 -8" strokeWidth={20} />
          <circle cx={36} cy={-30} r={5} fill={INK} stroke="none" />
          {growl > 0.01 && (
            <path d={`M64 ${-8} l8 ${6 + Math.abs(Math.sin(t * 2.4)) * 8} l8 -6`} stroke="#fff" strokeWidth={6} />
          )}
        </g>
      </g>
    </g>
  );
};

// ---------- Text overlay ----------
type Line = { text: string; color?: string; bg?: string };
const Caption: React.FC<{ lines: Line[]; top?: number; size?: number }> = ({
  lines,
  top = 330,
  size = 112,
}) => (
  <div
    style={{
      position: "absolute",
      top,
      left: 0,
      right: 0,
      display: "flex",
      flexDirection: "column",
      alignItems: "center",
      gap: 6,
    }}
  >
    {lines.map((l, i) => (
      <CaptionLine key={i} line={l} delay={6 + i * 10} size={size} />
    ))}
  </div>
);

const CaptionLine: React.FC<{ line: Line; delay: number; size: number }> = ({
  line,
  delay,
  size,
}) => {
  const p = useSpring(delay, 11, 150);
  return (
    <div
      style={{
        fontFamily: FONT,
        fontSize: size,
        lineHeight: 1.04,
        letterSpacing: 2,
        textAlign: "center",
        color: line.color ?? "#fff",
        background: line.bg,
        padding: line.bg ? "0 26px" : 0,
        WebkitTextStroke: line.bg ? undefined : `10px ${INK}`,
        paintOrder: "stroke fill",
        transform: `scale(${interpolate(p, [0, 1], [0.4, 1])}) rotate(${interpolate(p, [0, 1], [-5, 0])}deg)`,
        opacity: Math.min(1, p * 2),
      }}
    >
      {line.text}
    </div>
  );
};

// Small persistent series tag, kept inside the top safe zone.
const Tag: React.FC = () => (
  <div
    style={{
      position: "absolute",
      top: 250,
      left: 0,
      right: 0,
      textAlign: "center",
      fontFamily: FONT,
      fontSize: 38,
      letterSpacing: 8,
      color: "#fff",
      opacity: 0.85,
    }}
  >
    THE LAST FARM · PART 1
  </div>
);

const FadeIn: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const frame = useCurrentFrame();
  const o = interpolate(frame, [0, 8], [0, 1], clamp);
  return <AbsoluteFill style={{ opacity: o }}>{children}</AbsoluteFill>;
};

// ---------- Shared scenery ----------
const Stars: React.FC<{ n?: number }> = ({ n = 40 }) => (
  <>
    {Array.from({ length: n }).map((_, i) => (
      <circle
        key={i}
        cx={(i * 197) % W}
        cy={120 + ((i * 331) % 900)}
        r={2 + (i % 3)}
        fill="#fff"
        opacity={0.5}
      />
    ))}
  </>
);

const Farmhouse: React.FC<{ x: number; scale?: number; lit?: boolean }> = ({
  x,
  scale = 1,
  lit = true,
}) => (
  <g transform={`translate(${x} ${GROUND}) scale(${scale})`}>
    <g stroke={INK} strokeWidth={10} strokeLinejoin="round">
      <rect x={-170} y={-230} width={340} height={230} fill="#8a5a3a" />
      <path d="M-200 -230 L0 -370 L200 -230 Z" fill="#5a3322" />
      <rect x={-40} y={-120} width={80} height={120} fill="#3a2418" />
      <rect x={-140} y={-170} width={70} height={60} fill={lit ? "#ffc15a" : "#2b3350"} />
      <rect x={70} y={-170} width={70} height={60} fill={lit ? "#ffc15a" : "#2b3350"} />
    </g>
  </g>
);

const Ground: React.FC<{ color: string }> = ({ color }) => (
  <>
    <rect x={0} y={GROUND} width={W} height={H - GROUND} fill={color} />
    <path d={`M0 ${GROUND} H${W}`} stroke={INK} strokeWidth={10} />
  </>
);

const Fence: React.FC<{ from: number; to: number; y?: number; color?: string }> = ({
  from,
  to,
  y = GROUND,
  color = "#d8cfc0",
}) => {
  const posts = [];
  for (let x = from; x <= to; x += 90) posts.push(x);
  return (
    <g stroke={INK} strokeWidth={8} strokeLinecap="round">
      {posts.map((px) => (
        <rect key={px} x={px - 9} y={y - 150} width={18} height={150} fill={color} />
      ))}
      <path d={`M${from} ${y - 110} H${to} M${from} ${y - 55} H${to}`} stroke={color} strokeWidth={12} />
    </g>
  );
};

const Gate: React.FC<{ x: number; locked: number; color?: string }> = ({
  x,
  locked,
  color = "#d8cfc0",
}) => {
  const lock = spring({
    frame: locked,
    fps: 30,
    config: { damping: 9, stiffness: 200 },
  });
  return (
    <g>
      <g stroke={INK} strokeWidth={8} fill={color}>
        <rect x={x - 130} y={GROUND - 260} width={26} height={260} />
        <rect x={x + 104} y={GROUND - 260} width={26} height={260} />
      </g>
      <g stroke={color} strokeWidth={10} strokeLinecap="round" fill="none">
        <path d={`M${x - 104} ${GROUND - 220} H${x + 104} M${x - 104} ${GROUND - 70} H${x + 104}`} />
        {[-70, -35, 0, 35, 70].map((o) => (
          <path key={o} d={`M${x + o} ${GROUND - 220} V${GROUND - 70}`} />
        ))}
      </g>
      {locked >= 0 && (
        <g transform={`translate(${x} ${GROUND - 140 - (1 - lock) * 90}) scale(${0.4 + lock * 0.6})`}>
          <path
            d="M-24 -26 V-48 a24 24 0 0 1 48 0 V-26"
            stroke={INK}
            strokeWidth={12}
            fill="none"
            strokeLinecap="round"
          />
          <rect x={-42} y={-26} width={84} height={70} rx={12} fill="#f2c14e" stroke={INK} strokeWidth={8} />
          <circle cy={8} r={8} fill={INK} />
        </g>
      )}
    </g>
  );
};

const Table: React.FC<{ x: number; w?: number }> = ({ x, w = 360 }) => (
  <g stroke={INK} strokeWidth={9} strokeLinejoin="round">
    <rect x={x} y={GROUND - 170} width={w} height={26} rx={6} fill="#8B5A2B" />
    <rect x={x + 30} y={GROUND - 144} width={22} height={144} fill="#6b4220" />
    <rect x={x + w - 52} y={GROUND - 144} width={22} height={144} fill="#6b4220" />
  </g>
);

const Chair: React.FC<{ x: number }> = ({ x }) => (
  <g stroke={INK} strokeWidth={9} strokeLinecap="round" fill="none">
    <path d={`M${x - 40} ${GROUND - 95} H${x + 40} M${x - 36} ${GROUND - 95} V${GROUND} M${x + 36} ${GROUND - 95} V${GROUND} M${x - 40} ${GROUND - 95} V${GROUND - 250}`} />
  </g>
);

const duskSky = (
  <defs>
    <linearGradient id="dusk" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stopColor="#262538" />
      <stop offset="0.55" stopColor="#6a3a4a" />
      <stop offset="1" stopColor="#d0612f" />
    </linearGradient>
    <radialGradient id="glow" cx="0.5" cy="0.5" r="0.5">
      <stop offset="0" stopColor="#ff8a3a" stopOpacity="0.95" />
      <stop offset="1" stopColor="#ff8a3a" stopOpacity="0" />
    </radialGradient>
  </defs>
);

// ---------- Scenes 1 + 2: the farmhouse table ----------
const FarmScene: React.FC = () => {
  const frame = useCurrentFrame();
  const stand = useSpring(260 - 240 + 0, 15, 90);
  const up = spring({
    frame: frame - 252,
    fps: 30,
    config: { damping: 13, stiffness: 110 },
  });
  const breathe = Math.sin(frame / 14) * 3;
  const flicker = 0.85 + Math.sin(frame / 5) * 0.08 + Math.sin(frame / 11) * 0.05;
  const alert = interpolate(frame, [270, 300], [0, 1], clamp);
  const glowGrow = interpolate(frame, [0, 480], [1, 1.5], clamp);
  void stand;

  const pose = mixPose(SIT, STAND, up);
  const hipY = interpolate(up, [0, 1], [GROUND - 90, GROUND - 180]);
  const x = interpolate(up, [0, 1], [430, 400]);
  const lookPose: Pose = {
    ...pose,
    lean: pose.lean - alert * 4,
    rArm: pose.rArm + alert * 60 * up,
    rElbow: pose.rElbow - alert * 20 * up,
    lArm: pose.lArm + (up > 0.5 ? alert * 20 : 0),
  };
  return (
    <FadeIn>
      <AbsoluteFill>
        <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`}>
          {duskSky}
          <rect width={W} height={H} fill="url(#dusk)" />
          <ellipse
            cx={900}
            cy={GROUND - 20}
            rx={420 * glowGrow}
            ry={330 * glowGrow}
            fill="url(#glow)"
            opacity={flicker}
          />
          <Stars n={22} />
          <Ground color="#4a3426" />
          <Farmhouse x={120} scale={0.85} />
          <Chair x={400} />
          <Table x={470} />
          <Dog
            x={300}
            headUp={alert}
            wag={Math.sin(frame / 3)}
            t={frame}
            dir={1}
          />
          <Figure x={x} hipY={hipY + breathe * (1 - up)} pose={lookPose} />
          {alert > 0.2 && (
            <text
              x={380}
              y={GROUND - 250}
              fontFamily={FONT}
              fontSize={90}
              fill="#ffd34e"
              stroke={INK}
              strokeWidth={8}
              paintOrder="stroke"
              textAnchor="middle"
              opacity={alert}
            >
              !
            </text>
          )}
        </svg>
        <Tag />
        <Sequence from={0} durationInFrames={240}>
          <Caption
            lines={[
              { text: "IF THE ZOMBIE" },
              { text: "APOCALYPSE", bg: "#2E9E4F" },
              { text: "STARTED TODAY..." },
            ]}
          />
        </Sequence>
        <Sequence from={240} durationInFrames={240}>
          <Caption
            lines={[
              { text: "HE ONLY HAD" },
              { text: "ONE CHOICE.", bg: "#D7263D" },
            ]}
          />
        </Sequence>
      </AbsoluteFill>
    </FadeIn>
  );
};

// ---------- Scene 3: lock the gate ----------
const GateScene: React.FC = () => {
  const frame = useCurrentFrame();
  const move = spring({
    frame: frame - 20,
    fps: 30,
    config: { damping: 22, stiffness: 55 },
  });
  const x = interpolate(move, [0, 1], [110, 745]);
  const speed = Math.abs(interpolate(move, [0, 0.5, 1], [0, 1, 0]));
  const running = frame > 20 && move < 0.985;
  const phase = (frame - 20) * 0.6;
  const reach = interpolate(frame, [110, 135], [0, 1], clamp);
  const base = running ? runPose(phase) : STAND;
  const pose: Pose = {
    ...base,
    lean: running ? base.lean : 8,
    rArm: base.rArm + reach * (85 - base.rArm),
    rElbow: base.rElbow + reach * (0 - base.rElbow),
    lArm: running ? base.lArm : -8,
  };
  void speed;
  const dogMove = spring({
    frame: frame - 34,
    fps: 30,
    config: { damping: 24, stiffness: 50 },
  });
  const dogX = interpolate(dogMove, [0, 1], [30, 560]);
  const dogRunning = frame > 34 && dogMove < 0.98;
  const bob = dogRunning ? Math.abs(Math.sin(frame / 2.4)) * 10 : 0;
  const clickT = frame - 140;
  return (
    <FadeIn>
      <AbsoluteFill>
        <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`}>
          {duskSky}
          <rect width={W} height={H} fill="url(#dusk)" />
          <ellipse cx={940} cy={GROUND - 20} rx={560} ry={360} fill="url(#glow)" opacity={0.9} />
          <Stars n={22} />
          <Ground color="#4a3426" />
          <Farmhouse x={140} scale={0.7} />
          <Fence from={260} to={800} />
          <Fence from={960} to={1100} />
          <Gate x={880} locked={clickT} />
          <g transform={`translate(0 ${-bob})`}>
            <Dog
              x={dogX}
              headUp={0}
              wag={Math.sin(frame / 3)}
              t={frame}
              crouch={dogRunning ? 0 : 0}
            />
          </g>
          <Figure
            x={x}
            hipY={GROUND - 180 - (running ? Math.abs(Math.sin(phase)) * 14 : 0)}
            pose={pose}
          />
          {clickT > 0 && clickT < 40 && (
            <text
              x={880}
              y={GROUND - 330 - clickT}
              fontFamily={FONT}
              fontSize={70}
              fill="#ffd34e"
              stroke={INK}
              strokeWidth={8}
              paintOrder="stroke"
              textAnchor="middle"
              opacity={interpolate(clickT, [0, 8, 40], [0, 1, 0], clamp)}
              transform={`rotate(-8 880 ${GROUND - 330 - clickT})`}
            >
              CLICK!
            </text>
          )}
        </svg>
        <Tag />
        <Caption
          lines={[
            { text: "LOCK" },
            { text: "EVERYTHING.", bg: "#F2C14E", color: INK },
          ]}
        />
      </AbsoluteFill>
    </FadeIn>
  );
};

// ---------- Scene 4: the map ----------
const ZONES = [
  { label: "WATER", x: 250, y: 780, color: "#1D74D6", icon: <path d="M0 -38 Q38 8 0 34 Q-38 8 0 -38Z" fill="#1D74D6" /> },
  { label: "FOOD", x: 750, y: 740, color: "#E0A100", icon: (
    <g><circle r={28} fill="#D7263D" stroke={INK} strokeWidth={6} /><path d="M0 -28 q4 -16 18 -18" stroke="#2E9E4F" strokeWidth={8} fill="none" strokeLinecap="round" /></g>
  ) },
  { label: "TOOLS", x: 300, y: 1060, color: "#6b7078", icon: (
    <g stroke="#6b7078" strokeWidth={12} strokeLinecap="round" fill="none"><path d="M-28 28 L22 -22" /><path d="M8 -38 L38 -8" strokeWidth={22} /></g>
  ) },
  { label: "FUEL", x: 760, y: 1040, color: "#D7263D", icon: (
    <g fill="#D7263D" stroke={INK} strokeWidth={6}><rect x={-26} y={-26} width={52} height={58} rx={8} /><rect x={-6} y={-40} width={24} height={14} /></g>
  ) },
];

const MapScene: React.FC = () => {
  const frame = useCurrentFrame();
  const card = useSpring(4, 13, 100);
  const route = interpolate(frame, [60, 260], [0, 1], {
    ...clamp,
    easing: Easing.inOut(Easing.quad),
  });
  const circle = interpolate(frame, [270, 310], [0, 1], clamp);
  const len = 1800;
  const point = interpolate(frame, [20, 40], [0, 1], clamp);
  const wobble = Math.sin(frame / 9) * 2;
  const pose: Pose = {
    ...STAND,
    lean: 12,
    rArm: 120 + wobble * 3,
    rElbow: -30,
    lArm: 10,
  };
  const pulses = (i: number) => spring({ frame: frame - (50 + i * 55), fps: 30, config: { damping: 9, stiffness: 160 } });
  return (
    <FadeIn>
      <AbsoluteFill>
        <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`}>
          {duskSky}
          <rect width={W} height={H} fill="url(#dusk)" />
          <Stars n={18} />
          <Ground color="#4a3426" />
          <Table x={440} w={520} />
          {/* map card */}
          <g transform={`translate(540 830) rotate(-2) scale(${card * 0.72}) translate(-540 -880)`}>
            <rect x={90} y={570} width={900} height={620} rx={22} fill="#efe3c6" stroke={INK} strokeWidth={10} />
            <path d="M110 590 H500 Q440 760 520 870 Q400 980 360 1170 H110 Z" fill="#bfe3b4" opacity={0.7} />
            <path d="M970 590 H650 Q610 720 700 800 Q760 900 700 1170 H970 Z" fill="#9ccbf2" opacity={0.7} />
            <g stroke="#00000020" strokeWidth={3}>
              {[240, 390, 540, 690, 840].map((gx) => <path key={gx} d={`M${gx} 590 V1170`} />)}
              {[720, 850, 980, 1100].map((gy) => <path key={gy} d={`M110 ${gy} H970`} />)}
            </g>
            <path
              d="M250 780 Q500 720 750 740 Q860 900 760 1040 Q540 1130 300 1060"
              stroke="#D7263D"
              strokeWidth={12}
              strokeDasharray="30 20"
              strokeLinecap="round"
              fill="none"
              pathLength={len}
              style={{ strokeDashoffset: len * (1 - route) }}
            />
            {ZONES.map((z, i) => {
              const p = pulses(i);
              return (
                <g key={z.label} transform={`translate(${z.x} ${z.y}) scale(${p})`}>
                  <circle r={68} fill="#fff" stroke={INK} strokeWidth={8} />
                  {z.icon}
                  <rect x={-84} y={78} width={168} height={52} rx={12} fill={z.color} stroke={INK} strokeWidth={6} />
                  <text y={118} textAnchor="middle" fontFamily={FONT} fontSize={40} fill="#fff" letterSpacing={2}>
                    {z.label}
                  </text>
                </g>
              );
            })}
            <ellipse
              cx={540}
              cy={900}
              rx={430}
              ry={290}
              fill="none"
              stroke="#D7263D"
              strokeWidth={10}
              strokeLinecap="round"
              pathLength={1}
              strokeDasharray={1}
              style={{ strokeDashoffset: 1 - circle }}
            />
          </g>
          <Figure x={250} hipY={GROUND - 180} pose={{ ...pose, rArm: pose.rArm * point + STAND.rArm * (1 - point) }} />
        </svg>
        <Tag />
        <Caption
          size={104}
          lines={[
            { text: "WATER. FOOD.", color: "#fff" },
            { text: "TOOLS. FUEL.", color: "#F2C14E" },
          ]}
        />
      </AbsoluteFill>
    </FadeIn>
  );
};

// ---------- Scene 5: night ----------
const NightScene: React.FC = () => {
  const frame = useCurrentFrame();
  const approach = interpolate(frame, [20, 340], [1140, 960], {
    ...clamp,
    easing: Easing.inOut(Easing.quad),
  });
  const phase = frame * 0.14;
  const growl = interpolate(frame, [90, 110], [0, 1], clamp);
  const flash = interpolate(frame % 90, [0, 6, 14], [0.15, 0.5, 0.15], clamp);
  return (
    <FadeIn>
      <AbsoluteFill>
        <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`}>
          <defs>
            <linearGradient id="night" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0" stopColor="#070a1a" />
              <stop offset="0.7" stopColor="#1b2146" />
              <stop offset="1" stopColor="#5a2a3a" />
            </linearGradient>
            <radialGradient id="haze" cx="0.5" cy="0.5" r="0.5">
              <stop offset="0" stopColor="#e0572e" stopOpacity="0.9" />
              <stop offset="1" stopColor="#e0572e" stopOpacity="0" />
            </radialGradient>
          </defs>
          <rect width={W} height={H} fill="url(#night)" />
          <Stars n={60} />
          <circle cx={860} cy={800} r={70} fill="#f2efe0" opacity={0.95} />
          <ellipse cx={960} cy={GROUND - 120} rx={520} ry={420} fill="url(#haze)" />
          <Ground color="#1a1620" />
          <Farmhouse x={150} scale={0.8} lit={false} />
          {/* distant silhouette */}
          <g opacity={0.95}>
            <Figure
              x={approach}
              hipY={GROUND - 15 - 180 * 0.62}
              pose={zombiePose(phase)}
              dir={-1}
              scale={0.62}
              color="#000"
              halo={false}
              sw={14}
            />
          </g>
          <Fence from={300} to={700} color="#9aa0b8" />
          <Gate x={820} locked={0} color="#9aa0b8" />
          <Dog x={520} dir={1} headUp={0.4} growl={growl} crouch={growl} wag={0} t={frame} />
          <Figure x={330} hipY={GROUND - 180} pose={{ ...STAND, rArm: 20, lArm: 12, lean: 6 }} />
          {growl > 0.4 && (
            <text
              x={560}
              y={GROUND - 190}
              fontFamily={FONT}
              fontSize={44}
              fill="#fff"
              stroke={INK}
              strokeWidth={7}
              paintOrder="stroke"
              opacity={flash + 0.5}
              textAnchor="middle"
            >
              grrr...
            </text>
          )}
        </svg>
        <Tag />
        <Caption
          size={108}
          lines={[
            { text: "BUT SOMETHING" },
            { text: "WAS ALREADY", bg: "#D7263D" },
            { text: "COMING." },
          ]}
        />
      </AbsoluteFill>
    </FadeIn>
  );
};

// Narration: one line per caption, starting as the caption pops in.
const NARRATION = [
  { file: "narration/s1.mp3", from: 12 },
  { file: "narration/s2.mp3", from: 252 },
  { file: "narration/s3.mp3", from: 492 },
  { file: "narration/s4.mp3", from: 792 },
  { file: "narration/s5.mp3", from: 1152 },
];

export const LastFarm: React.FC = () => (
  <AbsoluteFill style={{ background: "#000" }}>
    {NARRATION.map((n) => (
      <Sequence key={n.file} from={n.from} durationInFrames={150}>
        <Audio src={staticFile(n.file)} />
      </Sequence>
    ))}
    <Sequence from={0} durationInFrames={480}>
      <FarmScene />
    </Sequence>
    <Sequence from={480} durationInFrames={300}>
      <GateScene />
    </Sequence>
    <Sequence from={780} durationInFrames={360}>
      <MapScene />
    </Sequence>
    <Sequence from={1140} durationInFrames={360}>
      <NightScene />
    </Sequence>
  </AbsoluteFill>
);
