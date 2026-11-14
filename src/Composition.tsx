import React from "react";
import {
  AbsoluteFill,
  Easing,
  interpolate,
  Sequence,
  spring,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import { Actor, pxPerUnit, SplatStage } from "./SplatStage";

const INK = "#1b1b1f";
const STROKE = 12;

const useIn = (delay: number) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  return spring({
    frame: frame - delay,
    fps,
    config: { damping: 12, stiffness: 140 },
  });
};

const Word: React.FC<{
  text: string;
  delay: number;
  color?: string;
  size?: number;
  bg?: string;
}> = ({ text, delay, color = INK, size = 150, bg }) => {
  const p = useIn(delay);
  return (
    <div
      style={{
        fontFamily: "Impact, 'Arial Black', sans-serif",
        fontSize: size,
        lineHeight: 1.02,
        color,
        background: bg,
        padding: bg ? "0 28px" : 0,
        textAlign: "center",
        transform: `scale(${interpolate(p, [0, 1], [0.3, 1])}) rotate(${interpolate(p, [0, 1], [-6, 0])}deg)`,
        opacity: Math.min(1, p * 2),
        letterSpacing: 2,
      }}
    >
      {text}
    </div>
  );
};

// Maps the 1080x1920 frame onto the splat stage: feet at y=0 land on `floorPx`.
const px = (x: number) => (x - 540) / pxPerUnit(1920);

const Splats: React.FC<{ floorPx: number; actors: Actor[] }> = ({
  floorPx,
  actors,
}) => (
  <SplatStage width={1080} height={1920} floorPx={floorPx} actors={actors} />
);

const Stick: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <g
    stroke={INK}
    strokeWidth={STROKE}
    strokeLinecap="round"
    strokeLinejoin="round"
    fill="none"
  >
    {children}
  </g>
);

// ---------- Scene 1 ----------
const Scene1: React.FC = () => {
  const frame = useCurrentFrame();
  const wag = Math.sin(frame / 3) * 18;
  const slide = interpolate(useIn(0), [0, 1], [60, 0]);

  return (
    <AbsoluteFill style={{ background: "#F6F0E4" }}>
      <div
        style={{
          position: "absolute",
          top: 150,
          left: 0,
          right: 0,
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          gap: 10,
        }}
      >
        <Word text="IF THE" delay={6} size={130} />
        <Word text="ZOMBIE" delay={16} size={230} color="#fff" bg="#2E9E4F" />
        <Word text="APOCALYPSE" delay={26} size={140} />
      </div>

      <svg
        width={1080}
        height={1920}
        viewBox="0 0 1080 1920"
        style={{ position: "absolute", transform: `translateY(${slide}px)` }}
      >
        {/* floor */}
        <rect x={0} y={1560} width={1080} height={360} fill="#E3D8C2" />
      </svg>
      <Splats
        floorPx={1560}
        actors={[
          {
            file: "survivor",
            beats: [{ clip: "idle", from: 0 }],
            position: () => [px(110), 0, 0],
            rotationY: () => 0.5,
          },
        ]}
      />
      <svg
        width={1080}
        height={1920}
        viewBox="0 0 1080 1920"
        style={{ position: "absolute", transform: `translateY(${slide}px)` }}
      >
        {/* table */}
        <rect x={200} y={1300} width={760} height={30} rx={8} fill="#8B5A2B" />
        <rect x={250} y={1330} width={26} height={230} fill="#8B5A2B" />
        <rect x={884} y={1330} width={26} height={230} fill="#8B5A2B" />
        {/* dog on table */}
        <g transform="translate(700 1300)">
          <Stick>
            {/* body */}
            <path d="M-80 -20 Q-80 -110 0 -110 Q70 -110 70 -20 Z" fill="#C98B4E" />
            {/* tail */}
            <path d={`M-80 -60 Q${-130 + wag} ${-100} ${-120 + wag} ${-150}`} />
            {/* head */}
            <circle cx={50} cy={-150} r={48} fill="#C98B4E" />
            <path d="M18 -185 L10 -130" strokeWidth={22} />
            <path d="M80 -185 L92 -135" strokeWidth={22} />
          </Stick>
          <circle cx={62} cy={-158} r={6} fill={INK} />
          <circle cx={42} cy={-120} r={9} fill={INK} />
        </g>
      </svg>
    </AbsoluteFill>
  );
};

// ---------- Scene 2 ----------
type Item = {
  label: string;
  x: number;
  y: number;
  delay: number;
  color: string;
  icon: React.ReactNode;
};

const ITEMS: Item[] = [
  {
    label: "LAND",
    x: 330,
    y: 760,
    delay: 40,
    color: "#2E9E4F",
    icon: <path d="M-40 25 L-10 -30 L15 5 L30 -15 L50 25 Z" fill="#2E9E4F" />,
  },
  {
    label: "WATER",
    x: 760,
    y: 900,
    delay: 70,
    color: "#1D74D6",
    icon: <path d="M0 -40 Q40 10 0 35 Q-40 10 0 -40Z" fill="#1D74D6" />,
  },
  {
    label: "TOOLS",
    x: 300,
    y: 1230,
    delay: 100,
    color: "#7A7F87",
    icon: (
      <g stroke="#7A7F87" strokeWidth={12} strokeLinecap="round" fill="none">
        <path d="M-30 30 L25 -25" />
        <path d="M10 -40 L40 -10" strokeWidth={22} />
      </g>
    ),
  },
  {
    label: "FUEL",
    x: 760,
    y: 1260,
    delay: 130,
    color: "#E4572E",
    icon: (
      <g fill="#E4572E">
        <rect x={-28} y={-28} width={56} height={62} rx={8} />
        <rect x={-6} y={-42} width={26} height={16} />
      </g>
    ),
  },
  {
    label: "FOOD",
    x: 540,
    y: 1060,
    delay: 160,
    color: "#E0A100",
    icon: (
      <g>
        <circle r={30} fill="#D7263D" />
        <path d="M0 -30 q4 -18 18 -20" stroke="#2E9E4F" strokeWidth={8} fill="none" strokeLinecap="round" />
      </g>
    ),
  },
];

const Pin: React.FC<{ item: Item }> = ({ item }) => {
  const frame = useCurrentFrame();
  const p = useIn(item.delay);
  const bounce = Math.sin((frame - item.delay) / 8) * 4;
  return (
    <g
      transform={`translate(${item.x} ${item.y + bounce}) scale(${p})`}
      opacity={Math.min(1, p * 2)}
    >
      <circle r={78} fill="#fff" stroke={INK} strokeWidth={8} />
      {item.icon}
      <rect x={-95} y={95} width={190} height={62} rx={14} fill={item.color} />
      <text
        y={141}
        textAnchor="middle"
        fontFamily="Impact, 'Arial Black', sans-serif"
        fontSize={46}
        fill="#fff"
        letterSpacing={2}
      >
        {item.label}
      </text>
    </g>
  );
};

const Scene2: React.FC = () => {
  const frame = useCurrentFrame();
  const draw = interpolate(frame, [20, 190], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.inOut(Easing.quad),
  });
  const pathLen = 2200;
  const mapIn = useIn(0);

  return (
    <AbsoluteFill style={{ background: "#DDE8F2" }}>
      <div
        style={{
          position: "absolute",
          top: 140,
          left: 0,
          right: 0,
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
        }}
      >
        <Word text="KNOW YOUR" delay={4} size={120} />
        <Word text="MAP" delay={12} size={240} color="#fff" bg="#1D74D6" />
      </div>
      <svg width={1080} height={1920} viewBox="0 0 1080 1920">
        <g
          transform={`translate(540 1010) scale(${mapIn}) translate(-540 -1010)`}
        >
          <rect x={90} y={560} width={900} height={900} rx={30} fill="#F6F0E4" stroke={INK} strokeWidth={12} />
          {/* land */}
          <path
            d="M110 580 H620 Q560 760 640 880 Q500 1000 420 1100 Q320 1200 330 1440 H110 Z"
            fill="#BFE3B4"
          />
          {/* water */}
          <path
            d="M990 580 H700 Q650 760 720 880 Q830 960 800 1100 Q900 1200 840 1440 H990 Z"
            fill="#9CCBF2"
          />
          {/* grid */}
          <g stroke="#00000018" strokeWidth={3}>
            {[240, 390, 540, 690, 840].map((x) => (
              <path key={x} d={`M${x} 580 V1440`} />
            ))}
            {[720, 860, 1000, 1140, 1290].map((y) => (
              <path key={y} d={`M110 ${y} H970`} />
            ))}
          </g>
          {/* route */}
          <path
            d="M330 760 Q540 800 540 1060 Q300 1120 300 1230 Q540 1300 760 1260 Q820 1100 760 900"
            stroke="#D7263D"
            strokeWidth={10}
            strokeDasharray="28 20"
            fill="none"
            strokeLinecap="round"
            pathLength={pathLen}
            style={{ strokeDashoffset: pathLen * (1 - draw) }}
            opacity={0.9}
          />
        </g>
        {frame > 20 && ITEMS.map((it) => <Pin key={it.label} item={it} />)}
      </svg>
      <Splats
        floorPx={1640}
        actors={[
          {
            file: "survivor",
            beats: [
              { clip: "idle", from: 0 },
              { clip: "point", from: 30 },
            ],
            position: () => [px(150), 0, 1],
            rotationY: () => 0.9,
          },
        ]}
      />
      <div
        style={{
          position: "absolute",
          bottom: 190,
          left: 0,
          right: 0,
          display: "flex",
          justifyContent: "center",
        }}
      >
        <Word text="LAND • WATER • TOOLS" delay={190} size={64} />
      </div>
    </AbsoluteFill>
  );
};

// ---------- Scene 3 ----------
const Scene3: React.FC = () => {
  const frame = useCurrentFrame();
  const walk = interpolate(frame, [0, 60], [-120, 270], {
    extrapolateRight: "clamp",
  });
  const close = interpolate(frame, [30, 70], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.out(Easing.cubic),
  });
  const lock = useIn(85);
  const shackle = interpolate(frame, [85, 110], [-26, 0], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const zx = interpolate(frame, [0, 180], [1250, 880], {
    extrapolateRight: "clamp",
  });
  const locked = frame > 110;
  const turn = interpolate(frame, [118, 132], [Math.PI / 2, 0], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  return (
    <AbsoluteFill style={{ background: "#2B2D42" }}>
      <svg
        width={1080}
        height={1920}
        viewBox="0 0 1080 1920"
        style={{ position: "absolute" }}
      >
        <rect x={0} y={1380} width={1080} height={540} fill="#3A3D5C" />
      </svg>
      <Splats
        floorPx={1380}
        actors={[
          {
            file: "zombie",
            beats: [{ clip: "zombie", from: 0 }],
            position: () => [px(zx), 0, -0.5],
            rotationY: () => -Math.PI / 2,
          },
          {
            file: "survivor",
            beats: [
              { clip: "walk", from: 0 },
              { clip: "push", from: 56 },
              { clip: "cheer", from: 120 },
            ],
            position: () => [px(walk), 0, 0.5],
            rotationY: () => turn,
          },
        ]}
      />
      <div
        style={{
          position: "absolute",
          top: 150,
          left: 0,
          right: 0,
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
        }}
      >
        <Word text="LOCK" delay={10} size={200} color="#fff" />
        <Word text="THE GATE" delay={22} size={170} color="#fff" bg="#D7263D" />
      </div>
      <svg
        width={1080}
        height={1920}
        viewBox="0 0 1080 1920"
        style={{ position: "absolute" }}
      >
        {/* fence + posts */}
        <g stroke="#C9CEDB" strokeWidth={14} strokeLinecap="round">
          <path d="M470 1380 V900 M880 1380 V900" strokeWidth={28} />
          <path d="M880 1000 H1100 M880 1300 H1100 M980 1380 V950 M1060 1380 V950" />
        </g>
        {/* gate doors */}
        <g stroke="#C9CEDB" strokeWidth={12} strokeLinecap="round" fill="none">
          <g transform={`translate(484 0) scale(${close * 0.5 + 0.5} 1)`}>
            <path d="M0 1000 H205 M0 1300 H205 M0 1000 V1300 M205 1000 V1300 M70 1000 V1300 M140 1000 V1300" />
          </g>
          <g transform={`translate(866 0) scale(${-(close * 0.5 + 0.5)} 1)`}>
            <path d="M0 1000 H205 M0 1300 H205 M0 1000 V1300 M205 1000 V1300 M70 1000 V1300 M140 1000 V1300" />
          </g>
        </g>
        {/* padlock */}
        <g transform={`translate(675 1150) scale(${lock})`}>
          <path
            d={`M-26 -30 V-${50 + shackle} a26 26 0 0 1 52 0 V-30`}
            stroke="#F2C14E"
            strokeWidth={12}
            fill="none"
            strokeLinecap="round"
          />
          <rect x={-44} y={-30} width={88} height={72} rx={12} fill="#F2C14E" />
          <circle cy={4} r={8} fill={INK} />
        </g>
        {locked && (
          <text
            x={400}
            y={1000}
            fontFamily="Impact, 'Arial Black', sans-serif"
            fontSize={60}
            fill="#F2C14E"
            textAnchor="middle"
            transform={`rotate(-8 400 1000)`}
          >
            CLICK!
          </text>
        )}
      </svg>
      <div
        style={{
          position: "absolute",
          bottom: 190,
          left: 0,
          right: 0,
          display: "flex",
          justifyContent: "center",
        }}
      >
        <Word text="STAY SAFE." delay={125} size={120} color="#F2C14E" />
      </div>
    </AbsoluteFill>
  );
};

export const MyComponent: React.FC = () => {
  return (
    <AbsoluteFill>
      <Sequence from={0} durationInFrames={210}>
        <Scene1 />
      </Sequence>
      <Sequence from={210} durationInFrames={210}>
        <Scene2 />
      </Sequence>
      <Sequence from={420} durationInFrames={180}>
        <Scene3 />
      </Sequence>
    </AbsoluteFill>
  );
};
