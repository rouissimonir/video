import { Composition } from "remotion";
import { MyComponent } from "./Composition";
import { Explainer } from "./Explainer";
import { LastFarm } from "./LastFarm";
import { LastFarmV2, LAST_FARM_V2_FRAMES } from "./LastFarmV2";

export const RemotionRoot: React.FC = () => {
  return (
    <>
      <Composition
        id="ZombieApocalypse"
        component={MyComponent}
        durationInFrames={600}
        fps={30}
        width={1080}
        height={1920}
      />
      <Composition
        id="ClaudeCodeExplainer"
        component={Explainer}
        durationInFrames={900}
        fps={30}
        width={1080}
        height={1920}
      />
      <Composition
        id="LastFarm"
        component={LastFarm}
        durationInFrames={1500}
        fps={30}
        width={1080}
        height={1920}
      />
      <Composition
        id="LastFarmV2"
        component={LastFarmV2}
        durationInFrames={LAST_FARM_V2_FRAMES}
        fps={30}
        width={1080}
        height={1920}
      />
    </>
  );
};
