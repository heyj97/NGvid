# .ngvid 프로젝트 형식 (version 1)

NGvid 프로젝트는 폴더 하나다. 폴더째 옮겨도 열린다.

```
내프로젝트/
  project.ngvid     ← 편집 정보 (JSON, UTF-8)
  media/            ← 가져온 원본. 원래 파일명 그대로
  luts/             ← 가져온 LUT(.cube) 파일
  proxy/            ← 프록시 사본(`원본ID.mp4`). 지워도 다시 만들 수 있음
  backups/          ← 자동 백업(`project-날짜-시각.ngvid`). 지워도 됨
```

저장·읽기 코드는 `src/NGL.IO/ProjectSerializer.cs`, 데이터 모델은 `src/NGL.Core`에 있다.

## 규칙

- 속성 이름은 camelCase다.
- 시간은 `"시:분:초.소수"` 문자열이다. 예: `"00:01:05.5000000"`은 1분 5.5초. 프레임 번호가 아니라 시간으로 적기 때문에 fps를 바꿔도 클립 위치가 변하지 않는다.
- ID는 GUID 문자열이다. 클립은 `assetId`로 `assets`의 원본을 가리킨다.
- 원본 경로(`relativePath`)는 프로젝트 폴더 기준 상대 경로이고 구분자는 `/`다.
- 트랙과 클립은 `type`에 클래스 이름을 적어 종류를 구분한다.
- `version`이 이 프로그램이 아는 값보다 크면 열지 않는다. 형식을 바꿀 때는 `version`을 올리고 `ProjectSerializer.Deserialize`에서 이전 형식을 변환한다.

## 구조

| 위치 | 속성 | 설명 |
| --- | --- | --- |
| 최상위 | `version` | 형식 버전. 현재 `1` |
| | `name` | 프로젝트 이름 |
| | `sequence` | 시퀀스 (프로젝트당 하나) |
| | `assets` | 리소스 보관함의 원본 목록 |
| `sequence` | `preset` | `name`, `width`, `height`. 16:9(1920×1080), 9:16(1080×1920), 1:1(1080×1080) |
| | `frameRate` | `30` 또는 `60` |
| | `markers` | 마커 목록 |
| | `tracks` | 트랙 목록. 배열 순서가 트랙 순서이고 첫 트랙이 화면 맨 위에 그려진다 |
| 트랙 | `type` | `VideoTrack`, `AudioTrack`, `CaptionTrack` |
| | `id`, `name`, `color` | 색상은 `#RRGGBB` |
| | `locked`, `hidden`, `muted`, `solo` | 잠금, 숨김(영상·자막), 음소거·솔로(오디오) |
| | `clips` | 클립 목록 |
| 클립 | `type` | 아래 표 참고 |
| | `id` | |
| | `range` | 타임라인에서 차지하는 구간. `start`, `duration` |
| | `linkId` | 같은 값의 클립끼리 연결됨(영상과 그 소리). 없으면 적지 않는다 |
| | `groupId` | 같은 값의 클립끼리 그룹. 없으면 적지 않는다 |
| | `keyframes` | 속성별 키프레임. 키는 `X`·`Y`·`Scale`·`Rotation`·`Opacity`·`Volume`, 값은 `time`(클립 시작에서 잰 시간)·`value`·`easing`(`Linear`·`EaseIn`·`EaseOut`·`EaseInOut`)의 목록 |
| 마커 | `id`, `time`, `name` | `sequence.markers`의 항목 |
| 원본 | `id`, `kind` | `kind`는 `Video`, `Audio`, `Image` |
| | `name` | 보관함과 클립에 표시되는 이름 |
| | `relativePath` | 예: `media/interview.mp4` |
| | `duration` | 원본 길이. 이미지는 0 |
| | `hasAudio` | 영상 원본에 소리가 들어 있는지 |

### 클립 종류

| `type` | 올릴 수 있는 트랙 | 추가 속성 |
| --- | --- | --- |
| `VideoClip` | `VideoTrack` | `assetId`, `sourceStart`(원본에서 재생을 시작하는 위치), 변형 속성 |
| `ImageClip` | `VideoTrack` | `assetId` (`sourceStart`는 쓰지 않음), 변형 속성 |
| `TextClip` | `VideoTrack` | `text`, `style` |
| `AudioClip` | `AudioTrack` | `assetId`, `sourceStart`, `volume`(0~2), `fadeIn`, `fadeOut` |
| `CaptionClip` | `CaptionTrack` | `text`, `style` |

변형 속성(영상·이미지 클립): `x`, `y`(화면 가운데에서 옮긴 픽셀), `scale`(0.1~4), `rotation`(시계 방향, 도), `opacity`(0~1),
`blend`(`Normal`·`Multiply`·`Screen`·`Overlay`·`Add`), `fit`(`Fit`·`Fill`), `cropLeft`·`cropTop`·`cropRight`·`cropBottom`(0~0.95),
`flipHorizontal`, `flipVertical`.

영상·이미지 클립에는 효과(`effects`: `blur` 0~100, `sharpen`·`vignette` 0~1, `brightness`·`contrast`·`saturation`·`temperature` -1~1,
`exposure` -2~2, `lut` 프로젝트 폴더 기준 .cube 경로)와 전환(`transition`: `None`·`Dissolve`·`DipToBlack`·`Wipe`, `transitionDuration`)이 더 있다.

크로마키는 `effects`의 `keyColor`(없으면 적지 않음), `keyTolerance`, `keySoftness`(0~1)이고,
마스크는 `mask`: `shape`(`None`·`Rectangle`·`Ellipse`·`Polygon`), `x`·`y`·`width`·`height`(클립 그림에 대한 비율), `feather`(픽셀), `inverted`,
`points`(자유 도형의 점 `x`·`y` 목록)이다. 키프레임 속성에는 `MaskX`·`MaskY`·`MaskWidth`·`MaskHeight`도 쓸 수 있다.
영상·오디오 클립에는 재생 속도 `speed`(0.25~4, 없으면 1)가 있다. 클립의 `elapsed` 시점에 보이는 원본 위치는
`sourceStart + elapsed × speed`이고, 키프레임 속성 `Speed`가 있으면 `speed` 대신 그 값을 0부터 `elapsed`까지 쌓은 만큼이다.
원본(`assets`)에는 화면에 보이는 크기 `width`·`height`가 기록된다.

글자 스타일(`style`): `font`, `size`(픽셀), `bold`, `color`, `outlineColor`, `outlineWidth`, `shadowColor`, `shadowOffset`,
`boxColor`(없으면 적지 않음), `x`, `y`(화면 가운데에서 옮긴 픽셀), `highlightColor`(단어 강조 색, 없으면 적지 않음).

오디오 트랙에는 `volume`(0~2), `duckUnder`(자동 감쇠의 기준 트랙 ID), `duckLevel`(감쇠 중 볼륨 0~1)이 더 있다.

## 예시

```json
{
  "version": 1,
  "name": "여행 브이로그 1편",
  "sequence": {
    "preset": { "name": "16:9", "width": 1920, "height": 1080 },
    "frameRate": 30,
    "tracks": [
      {
        "type": "VideoTrack",
        "id": "0b0f6a3e-6f0e-4c0e-9a55-0d3c1f1f5a01",
        "name": "영상 1",
        "color": "#4C8DFF",
        "clips": [
          {
            "type": "VideoClip",
            "sourceStart": "00:00:05",
            "assetId": "5d2d7c1a-9a3b-4a53-8f0e-2f6f0a7c9b02",
            "id": "a3c1e2f4-1b2c-4d5e-8f90-1a2b3c4d5e03",
            "range": { "start": "00:00:00", "duration": "00:00:10" }
          }
        ]
      }
    ]
  },
  "assets": [
    {
      "id": "5d2d7c1a-9a3b-4a53-8f0e-2f6f0a7c9b02",
      "kind": "Video",
      "name": "interview",
      "relativePath": "media/interview.mp4",
      "duration": "00:01:30"
    }
  ]
}
```
