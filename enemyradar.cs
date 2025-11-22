using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;

namespace enemyradar
{
    // Duckov 로더가 찾는 엔트리 포인트:
    //   enemyradar.ModBehaviour
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                GameObject go = new GameObject("EnemyRadarRoot");
                UnityEngine.Object.DontDestroyOnLoad(go);

                go.AddComponent<EnemyRadarHUD>();

                Debug.Log("[EnemyRadar] ModBehaviour.OnAfterSetup - HUD 초기화 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[EnemyRadar] 초기화 예외: " + ex);
            }
        }
    }

    internal class EnemyRadarHUD : MonoBehaviour
    {
        // ===== HP 경고 언어 설정 =====
        private enum HpWarnLang
        {
            Auto = 0,
            Korean = 1,
            Japanese = 2,
            English = 3
        }

        private HpWarnLang _hpWarnLangMode = HpWarnLang.Auto;
        // ===== 플레이어 / 적 추적 =====
        private Transform _player;
        private readonly List<Transform> _enemies = new List<Transform>();

        // 적 / 전리품 스캔 타이머 분리
        private float _nextEnemyScanTime;
        private float _nextLootScanTime;

        private const float EnemyScanInterval = 3f;   // 적 레이더는 3초마다
        private const float LootScanInterval  = 1.5f; // 전리품 빔은 1.5초마다

        private bool _hasTarget;
        private Transform _nearestEnemy;
        private float _nearestDist;
        private float _normalizedDist;
        private float _enemyAngleDeg; // (디버그용) 가장 가까운 적 방향

        // ===== 거리 기준 (미터) =====
        // 0 ~ 7m    -> 2번 링 (가까움, 빨간 도넛 링)
        // 7 ~ 20m   -> 3번 링 (중간, 실선 피자조각)
        // 20 ~ 35m  -> 4번 링 (멀다, 점선 아크)
        private float _ring2DistanceMax = 7f;
        private float _ring3DistanceMax = 20f;
        private float _ring4DistanceMax = 35f;
        private float _maxRadarDistance = 35f;

        // ===== 텍스처 =====
        private Texture2D _radarTexture;   // 배경 원 + 외곽선
        private Texture2D _ring2Texture;   // 2번: 빨간 도넛 링
        private Texture2D _ring3Texture;   // 3번: 실선 피자조각
        private Texture2D _ring4Texture;   // 4번: 점선 아크
        private Texture2D _lootDotTexture; // 전리품 점용 동그라미

        private bool _texReady;

        // ===== 디버그 텍스트 스타일 =====
        private GUIStyle _labelStyle;
        private bool _styleReady;

        // ===== 플레이어 HP 경고 =====
        private MonoBehaviour _playerHealthMb;
        private FieldInfo _playerCurHpField;
        private FieldInfo _playerMaxHpField;
        private PropertyInfo _playerMaxHpProp;
        private float _playerHpRatio = 1f;
        private float _playerHpMaxObserved = 1f;
        private bool _hasHpRatio;
        private float _lowHpThreshold = 0.3f;
        private GUIStyle _lowHpStyle;

        // ===== team 필드 캐시 =====
        private readonly Dictionary<Type, FieldInfo> _teamFieldCache = new Dictionary<Type, FieldInfo>();

        // ===== 다수 각도 캐시 (3번/4번 링용) =====
        private readonly List<float> _midAngles = new List<float>();
        private readonly List<float> _farAngles = new List<float>();

        // Transform 요약 정보
        private class CharacterInfo
        {
            public Transform Tr;
            public string Team;
            public bool HasTeam;
            public bool IsPlayerTeam;
            public bool IsEnemyTeam;
        }

        // ===== 적 전리품 전용 정보 =====
        private class LootSpot
        {
            public Transform Tr;
            public int Tier; // 0=흰, 1=초록, 2=파랑, 3=보라, 4=금, 5=연빨, 6=진빨
        }

        private readonly List<LootSpot> _enemyLootSpots = new List<LootSpot>();

        // ───── 전리품 빔 월드 이펙트 ─────
        private GameObject _lootBeamRoot;
        private static Material _lootBeamMaterial;
        private readonly List<GameObject> _lootBeams = new List<GameObject>();

        // 빔 모양 설정값
        private float _lootBeamHeight  = 6f;    // 빔 높이
        private float _lootBeamWidth   = 0.25f; // 굵기
        private float _lootBeamOffsetY = 0.2f;  // 가방 위로 살짝 띄우기
        
        private static readonly string[] _lootContainerKeywords = new string[]
        {
    "lootbox_enemydie",
    "lootbox_natural",
    "container",
    "chest",
    "box",
    "drawer"
        };


        private void Start()
        {
            Debug.Log("[EnemyRadarHUD] Start - 준비 완료");

            // 전리품 빔 루트 오브젝트
            if (_lootBeamRoot == null)
            {
                _lootBeamRoot = new GameObject("EnemyLootBeamsRoot");
                UnityEngine.Object.DontDestroyOnLoad(_lootBeamRoot);
            }

            // 빔용 머티리얼 (Unlit/Color 우선, 없으면 Sprites/Default)
            if (_lootBeamMaterial == null)
            {
                Shader shader = Shader.Find("Unlit/Color");
                if (shader == null)
                    shader = Shader.Find("Sprites/Default");

                if (shader != null)
                {
                    _lootBeamMaterial = new Material(shader);
                    _lootBeamMaterial.renderQueue = 3000; // 투명 계열
                }
            }
        }

        private void Update()
        {
            float now = Time.time;

            // 적 / 플레이어 스캔 (3초마다)
            if (now >= _nextEnemyScanTime)
            {
                _nextEnemyScanTime = now + EnemyScanInterval;
                ScanCharacters();
            }

            // 전리품 가방 + 빔 스캔 (0.5초마다)
            if (now >= _nextLootScanTime)
            {
                _nextLootScanTime = now + LootScanInterval;
                ScanLootWorld();
            }

            // 가장 가까운 적 갱신
                        // 가장 가까운 적 갱신
            UpdateNearestEnemy();

            // ───── F7: HP 경고 언어 토글 ─────
            if (Input.GetKeyDown(KeyCode.F7))
            {
                int next = ((int)_hpWarnLangMode + 1) % 4;
                _hpWarnLangMode = (HpWarnLang)next;

                string label;
                switch (_hpWarnLangMode)
                {
                    case HpWarnLang.Korean:   label = "Korean";   break;
                    case HpWarnLang.Japanese: label = "Japanese"; break;
                    case HpWarnLang.English:  label = "English";  break;
                    default:                  label = "Auto";     break;
                }
            }


            // 플레이어 HP 비율 갱신
            UpdatePlayerHealthRatio();
        }

        private void OnGUI()
        {
            if (!_texReady)
                BuildTextures();
            if (!_styleReady)
                BuildStyle();
            if (_radarTexture == null)
                return;

            float size   = 200f;
            float margin = 20f;

            // ↓ 레이더 위치: 오른쪽 아래 (살짝 위로)
            float radarX = Screen.width  - size - margin;
            float radarY = Screen.height - size - margin - 80f;

            Rect radarRect = new Rect(radarX, radarY, size, size);

            Color prevColor = GUI.color;

            // 1) 레이더 배경
            GUI.color = Color.white;
            GUI.DrawTexture(radarRect, _radarTexture);

            // ─────────────────────────────────────
            // 카메라/플레이어 전방 벡터 미리 계산
            // ─────────────────────────────────────
            bool hasPlayer = (_player != null);
            Vector3 playerPos = Vector3.zero;
            Vector3 fwd = Vector3.forward;
            float fwdAngle = 0f;

            if (hasPlayer)
            {
                playerPos = _player.position;

                if (Camera.main != null)
                    fwd = Camera.main.transform.forward;
                else
                    fwd = _player.forward;

                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.0001f)
                    fwd = Vector3.forward;

                fwd.Normalize();
                fwdAngle = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            }

            // 2) 링 표시 (다수 적 지원) - ★ 백업본 구조 그대로 ★
            if (hasPlayer && _enemies.Count > 0)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 4f);
                float alpha = Mathf.Lerp(0.5f, 1.0f, pulse);
                GUI.color   = new Color(1f, 1f, 1f, alpha);

                bool hasRing2 = false;
                _midAngles.Clear();
                _farAngles.Clear();

                // 각 적 위치에 따라 링 분류
                for (int i = 0; i < _enemies.Count; i++)
                {
                    Transform tr = _enemies[i];
                    if (tr == null) continue;

                    Vector3 toEnemy = tr.position - playerPos;
                    float distance = toEnemy.magnitude;

                    if (distance <= _ring2DistanceMax)
                    {
                        // 2번 영역(가까움) 존재 여부만 체크
                        hasRing2 = true;
                        continue;
                    }

                    if (distance <= _ring3DistanceMax)
                    {
                        // 3번 영역(중간) → 실선 피자조각 여러 개
                        toEnemy.y = 0f;
                        if (toEnemy.sqrMagnitude < 0.0001f)
                            continue;
                        toEnemy.Normalize();

                        float enAngle = Mathf.Atan2(toEnemy.x, toEnemy.z) * Mathf.Rad2Deg;
                        float rel = Mathf.DeltaAngle(fwdAngle, enAngle); // -180~180, 앞이 0
                        _midAngles.Add(rel);
                    }
                    else if (distance <= _ring4DistanceMax)
                    {
                        // 4번 영역(멀다) → 점선 아크 여러 개
                        toEnemy.y = 0f;
                        if (toEnemy.sqrMagnitude < 0.0001f)
                            continue;
                        toEnemy.Normalize();

                        float enAngle = Mathf.Atan2(toEnemy.x, toEnemy.z) * Mathf.Rad2Deg;
                        float rel = Mathf.DeltaAngle(fwdAngle, enAngle);
                        _farAngles.Add(rel);
                    }
                }

                // 2번 링: 근접 적이 하나라도 있으면 중앙에 도넛 한 개
                if (hasRing2 && _ring2Texture != null)
                {
                    GUI.DrawTexture(radarRect, _ring2Texture);
                }

                // 3번 링: 중거리 적들 전부 방향별로 피자조각
                if (_ring3Texture != null)
                {
                    for (int i = 0; i < _midAngles.Count; i++)
                    {
                        float angle = _midAngles[i];
                        Matrix4x4 prevMatrix = GUI.matrix;
                        Vector2 pivot = new Vector2(
                            radarRect.x + radarRect.width * 0.5f,
                            radarRect.y + radarRect.height * 0.5f);

                        GUIUtility.RotateAroundPivot(angle, pivot);
                        GUI.DrawTexture(radarRect, _ring3Texture);
                        GUI.matrix = prevMatrix;
                    }
                }

                // 4번 링: 먼 적들 방향별 점선 아크
                if (_ring4Texture != null)
                {
                    for (int i = 0; i < _farAngles.Count; i++)
                    {
                        float angle = _farAngles[i];
                        Matrix4x4 prevMatrix = GUI.matrix;
                        Vector2 pivot = new Vector2(
                            radarRect.x + radarRect.width * 0.5f,
                            radarRect.y + radarRect.height * 0.5f);

                        GUIUtility.RotateAroundPivot(angle, pivot);
                        GUI.DrawTexture(radarRect, _ring4Texture);
                        GUI.matrix = prevMatrix;
                    }
                }

                GUI.color = prevColor;
            }
            else
            {
                GUI.color = prevColor;
            }

            // ─────────────────────────────────────
            // 3) 전리품 점(가방) 찍기
            //    - 적이 죽어서 떨어진 LootBox_EnemyDie_Template 만 사용
            //    - 상자 안의 "가장 높은 displayQuality" 로 색 결정
            // ─────────────────────────────────────
            if (hasPlayer && _enemyLootSpots.Count > 0 && _lootDotTexture != null)
            {
                float centerX = radarRect.x + radarRect.width * 0.5f;
                float centerY = radarRect.y + radarRect.height * 0.5f;

                for (int i = 0; i < _enemyLootSpots.Count; i++)
                {
                    LootSpot spot = _enemyLootSpots[i];
                    if (spot == null || spot.Tr == null)
                        continue;

                    int tier = spot.Tier;
                    // 등급 0 (흰색) 은 너무 지저분해질 수 있으니, 1 이상만 점 찍기
                    if (tier <= 2)
                        continue;

                    Vector3 toLoot = spot.Tr.position - playerPos;
                    toLoot.y = 0f;
                    float dist = toLoot.magnitude;
                    if (dist < 0.1f)
                        continue;
                    if (dist > _maxRadarDistance * 1.2f)
                        continue;

                    // 플레이어 전방 기준 각도
                    toLoot.Normalize();
                    float lootAngle = Mathf.Atan2(toLoot.x, toLoot.z) * Mathf.Rad2Deg;
                    float relAngle = Mathf.DeltaAngle(fwdAngle, lootAngle);

                    // 거리 → 반지름 (레이더 안쪽~바깥쪽까지)
                    float t = Mathf.Clamp01(dist / _maxRadarDistance);
                    // 0.25 ~ 0.9 사이에서 움직이게 (너무 가운데/테두리에 안붙게)
                    float radiusNorm = 0.25f + 0.65f * t;
                    float radarRadius = (radarRect.width * 0.5f) * radiusNorm;

                    // 각도 → 화면 좌표 (앞: 위쪽)
                    float rad = (90f - relAngle) * Mathf.Deg2Rad;
                    float px = centerX + Mathf.Cos(rad) * radarRadius;
                    float py = centerY - Mathf.Sin(rad) * radarRadius;

                    float dotSize = 7f;
                    Rect dotRect = new Rect(
                        px - dotSize * 0.5f,
                        py - dotSize * 0.5f,
                        dotSize,
                        dotSize);

                    Color old = GUI.color;
                    GUI.color = GetLootColorByTier(tier);
                    GUI.DrawTexture(dotRect, _lootDotTexture);
                    GUI.color = old;
                }
            }

            // 5) 플레이어 HP 낮을 때 하단 경고
            // 5) 플레이어 HP 낮을 때 하단 경고
            // 5) 플레이어 HP 낮을 때 하단 경고
            // 5) 플레이어 HP 낮을 때 하단 경고
            if (_hasHpRatio &&
                _playerHpMaxObserved > 0.01f &&
                _playerHpRatio > 0f &&
                _playerHpRatio <= _lowHpThreshold &&
                _lowHpStyle != null)
            {
                float boxWidth = 520f;
                float boxHeight = 60f;
                float boxX = (Screen.width - boxWidth) * 0.5f;
                float boxY = Screen.height - boxHeight - 180f;

                Rect bgRect = new Rect(boxX, boxY, boxWidth, boxHeight);

                Color PrevColor = GUI.color;

                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.Box(bgRect, GUIContent.none);

                GUI.color = Color.white;
                GUI.Label(bgRect, GetLowHpWarningText(), _lowHpStyle); // ← 여기!

                GUI.color = PrevColor;
            }



        }


        // ================== 캐릭터 스캔 ==================

        private FieldInfo GetTeamField(Type t)
        {
            FieldInfo fi;
            if (_teamFieldCache.TryGetValue(t, out fi))
                return fi;

            fi = t.GetField("team", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _teamFieldCache[t] = fi;
            return fi;
        }

        private string GetTeamStringFromField(MonoBehaviour mb, FieldInfo fi)
        {
            try
            {
                if (mb == null || fi == null) return "";
                object val = fi.GetValue(mb);
                if (val == null) return "";
                return val.ToString();
            }
            catch
            {
                return "";
            }
        }

        private void ScanCharacters()
        {
            _player = null;
            _enemies.Clear();

            MonoBehaviour[] all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            if (all == null || all.Length == 0)
            {
                _hasTarget = false;
                _nearestEnemy = null;
                Debug.Log("[EnemyRadarHUD] ScanCharacters - MonoBehaviour 없음");
                return;
            }

            // Transform -> CharacterInfo
            Dictionary<Transform, CharacterInfo> map = new Dictionary<Transform, CharacterInfo>();

            // pet / 환경물(브로큰월, 모래주머니, 커버, TombStone, 드럼통 등) Transform 및 그 부모 전체
            HashSet<Transform> envHierarchy = new HashSet<Transform>();

            foreach (MonoBehaviour mb in all)
            {
                if (mb == null) continue;

                Transform tr = mb.transform;
                if (tr == null) continue;

                string trNameLower = tr.name != null ? tr.name.ToLower() : "";
                string typeLower   = mb.GetType().Name.ToLower();

                bool isPetHere = false;
                bool isEnvHere = false; // BrokenWall / Sandbag / Cover / TombStone / Barrel 등

                // 이름/타입에 pet
                if (trNameLower.Contains("pet") || typeLower.Contains("pet"))
                {
                    isPetHere = true;
                }

                // 이름/타입으로 환경물 판정
                if (trNameLower.Contains("brokenwall")        ||
                    trNameLower.Contains("breakablewall")     ||
                    trNameLower.Contains("sandbag")           ||
                    trNameLower.Contains("sand bag")          ||
                    trNameLower.Contains("coverwall")         ||
                    trNameLower.Contains("cover_wall")        ||
                    trNameLower.Contains("cover")             ||
                    trNameLower.Contains("barricade")         ||
                    trNameLower.Contains("tombstone")         ||
                    trNameLower.Contains("explosive_oilbarrel_25") ||
                    trNameLower.Contains("explosive_oilbarrel")    ||
                    trNameLower.Contains("oilbarrel")         ||
                    trNameLower.Contains("testhalfobsticle_18")    ||
                    trNameLower.Contains("halfobsticle")      ||
                    trNameLower.Contains("obsticle")          ||
                    typeLower.Contains("brokenwall")          ||
                    typeLower.Contains("breakablewall")       ||
                    typeLower.Contains("sandbag")             ||
                    typeLower.Contains("sand bag")            ||
                    typeLower.Contains("coverwall")           ||
                    typeLower.Contains("cover_wall")          ||
                    typeLower.Contains("cover")               ||
                    typeLower.Contains("barricade")           ||
                    typeLower.Contains("tombstone")           ||
                    typeLower.Contains("explosive_oilbarrel_25") ||
                    typeLower.Contains("explosive_oilbarrel")    ||
                    typeLower.Contains("oilbarrel")           ||
                    typeLower.Contains("testhalfobsticle_18") ||
                    typeLower.Contains("halfobsticle")        ||
                    typeLower.Contains("obsticle"))
                {
                    isEnvHere = true;
                }

                FieldInfo tf = GetTeamField(mb.GetType());
                string teamStr = "";
                if (tf != null)
                {
                    teamStr = GetTeamStringFromField(mb, tf);
                    string lowerTeam = string.IsNullOrEmpty(teamStr) ? "" : teamStr.ToLower();

                    if (lowerTeam.Contains("pet"))
                        isPetHere = true;

                    if (lowerTeam.Contains("brokenwall")        ||
                        lowerTeam.Contains("breakablewall")     ||
                        lowerTeam.Contains("sandbag")           ||
                        lowerTeam.Contains("sand bag")          ||
                        lowerTeam.Contains("cover")             ||
                        lowerTeam.Contains("barricade")         ||
                        lowerTeam.Contains("tombstone")         ||
                        lowerTeam.Contains("explosive_oilbarrel_25") ||
                        lowerTeam.Contains("explosive_oilbarrel")    ||
                        lowerTeam.Contains("oilbarrel")         ||
                        lowerTeam.Contains("testhalfobsticle_18")    ||
                        lowerTeam.Contains("halfobsticle")      ||
                        lowerTeam.Contains("obsticle"))
                    {
                        isEnvHere = true;
                    }
                }

                // 펫 / 환경물(모래주머니, 커버, 브로큰월, TombStone, Barrel 등)은
                // 트랜스폼 + 부모 전부 무시 목록에 포함
                if (isPetHere || isEnvHere)
                {
                    Transform p = tr;
                    while (p != null)
                    {
                        if (!envHierarchy.Add(p))
                            break;
                        p = p.parent;
                    }
                }

                if (tf == null)
                    continue;

                CharacterInfo info;
                if (!map.TryGetValue(tr, out info))
                {
                    info = new CharacterInfo { Tr = tr };
                    map.Add(tr, info);
                }

                if (!string.IsNullOrEmpty(teamStr))
                {
                    info.Team = teamStr;
                    info.HasTeam = true;

                    string lowerT = teamStr.ToLower();

                    // 플레이어 팀
                    if (lowerT.Contains("player"))
                        info.IsPlayerTeam = true;

                    // 아군/중립 팀 추정 (이 문자열 포함하면 적 아님)
                    bool isAllyOrNeutral = false;
                    if (lowerT.Contains("ally")      ||
                        lowerT.Contains("friend")    ||
                        lowerT.Contains("friendly")  ||
                        lowerT.Contains("neutral")   ||
                        lowerT.Contains("civil")     ||
                        lowerT.Contains("shop")      ||
                        lowerT.Contains("vendor")    ||
                        lowerT.Contains("test")      ||
                        lowerT.Contains("dummy")     ||
                        lowerT.Contains("training"))
                    {
                        isAllyOrNeutral = true;
                    }

                    // 플레이어 팀도 아니고, 아군/중립도 아니면 "적 팀"으로 취급
                    if (!info.IsPlayerTeam && !isAllyOrNeutral)
                    {
                        info.IsEnemyTeam = true;
                    }
                }
            }

            // 후보 필터링: team 없는 애, 펫/환경물 계열은 제거
            List<CharacterInfo> chars = new List<CharacterInfo>();
            foreach (KeyValuePair<Transform, CharacterInfo> kv in map)
            {
                CharacterInfo info = kv.Value;
                if (!info.HasTeam) continue;
                if (info.Tr == null) continue;
                if (envHierarchy.Contains(info.Tr)) continue; // 펫 + 환경물 제거

                chars.Add(info);
            }

            if (chars.Count == 0)
            {
                _hasTarget = false;
                _nearestEnemy = null;
                Debug.Log("[EnemyRadarHUD] ScanCharacters - 유효 캐릭터 없음 (펫/환경물만 있거나 team 없음)");
                return;
            }

            // ───── 플레이어 Transform 결정 ─────
            Transform camTr = Camera.main != null ? Camera.main.transform : null;

            CharacterInfo playerInfo = null;
            float bestPlayerDist = float.MaxValue;

            // 1) team 에 player 가 들어있는 애들 중 카메라와 가장 가까운 것
            foreach (CharacterInfo info in chars)
            {
                if (!info.IsPlayerTeam) continue;

                float d;
                if (camTr != null)
                    d = Vector3.Distance(camTr.position, info.Tr.position);
                else
                    d = Vector3.Distance(Vector3.zero, info.Tr.position);

                if (d < bestPlayerDist)
                {
                    bestPlayerDist = d;
                    playerInfo = info;
                }
            }

            // 2) 위에서 못 찾았으면 그냥 카메라에 제일 가까운 애를 플레이어로
            if (playerInfo == null)
            {
                foreach (CharacterInfo info in chars)
                {
                    float d;
                    if (camTr != null)
                        d = Vector3.Distance(camTr.position, info.Tr.position);
                    else
                        d = Vector3.Distance(Vector3.zero, info.Tr.position);

                    if (d < bestPlayerDist)
                    {
                        bestPlayerDist = d;
                        playerInfo = info;
                    }
                }
            }

            _player = playerInfo != null ? playerInfo.Tr : null;

            // 플레이어 HP 컴포넌트 바인딩 (한 번만 설정)
            if (_playerHealthMb == null)
                SetupPlayerHealthAccessor(_player);

            // ───── 적 리스트 = "적 팀" + 플레이어 제외 ─────
            foreach (CharacterInfo info in chars)
            {
                if (info.Tr == null) continue;
                if (_player != null && info.Tr == _player) continue;
                if (!info.IsEnemyTeam) continue;

                _enemies.Add(info.Tr);
            }

            Debug.Log("[EnemyRadarHUD] ScanCharacters - 후보=" +
                      chars.Count + ", 펫/환경물 제외후 적수=" + _enemies.Count +
                      ", player=" + SafeGetName(_player));
        }

        private void UpdateNearestEnemy()
        {
            _hasTarget = false;
            _nearestEnemy = null;
            _nearestDist = 0f;
            _normalizedDist = 1f;
            _enemyAngleDeg = 0f;

            if (_player == null || _enemies.Count == 0)
                return;

            Vector3 playerPos = _player.position;
            float best = float.MaxValue;
            Transform bestEnemy = null;

            for (int i = 0; i < _enemies.Count; i++)
            {
                Transform tr = _enemies[i];
                if (tr == null) continue;

                float d = Vector3.Distance(playerPos, tr.position);
                if (d < best)
                {
                    best = d;
                    bestEnemy = tr;
                }
            }

            if (bestEnemy != null)
            {
                _hasTarget = true;
                _nearestEnemy = bestEnemy;
                _nearestDist = best;

                _maxRadarDistance = _ring4DistanceMax;
                if (_maxRadarDistance <= 0f) _maxRadarDistance = 1f;
                _normalizedDist = Mathf.Clamp01(_nearestDist / _maxRadarDistance);

                // (디버그용) 가장 가까운 적 방향
                Vector3 toEnemy = bestEnemy.position - playerPos;
                toEnemy.y = 0f;
                if (toEnemy.sqrMagnitude > 0.0001f)
                {
                    Vector3 fwd;
                    if (Camera.main != null)
                        fwd = Camera.main.transform.forward;
                    else
                        fwd = _player.forward;

                    fwd.y = 0f;
                    if (fwd.sqrMagnitude < 0.0001f)
                        fwd = Vector3.forward;

                    toEnemy.Normalize();
                    fwd.Normalize();

                    float fwdAngle = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                    float enAngle  = Mathf.Atan2(toEnemy.x, toEnemy.z) * Mathf.Rad2Deg;
                    float rel      = Mathf.DeltaAngle(fwdAngle, enAngle);

                    _enemyAngleDeg = rel;
                }
            }
        }

        private string SafeGetName(Transform tr)
        {
            if (tr == null) return "(null)";
            string n = tr.name;
            return string.IsNullOrEmpty(n) ? "(noname)" : n;
        }

        
        // ==================
        // ================== 플레이어 HP 접근 ==================

        private void SetupPlayerHealthAccessor(Transform playerTr)
        {
            _playerHealthMb = null;
            _playerCurHpField = null;
            _playerMaxHpField = null;
            _playerMaxHpProp = null;
            _hasHpRatio = false;
            _playerHpRatio = 1f;
            _playerHpMaxObserved = 1f;

            if (playerTr == null)
            {
                Debug.Log("[EnemyRadarHUD] SetupPlayerHealthAccessor - playerTr가 null");
                return;
            }

            try
            {
                // 씬 전체에서 Health 타입 후보 찾기
                MonoBehaviour[] all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                if (all == null || all.Length == 0)
                {
                    Debug.Log("[EnemyRadarHUD] SetupPlayerHealthAccessor - MonoBehaviour가 없음");
                    return;
                }

                MonoBehaviour bestMb = null;
                float bestDist = float.MaxValue;
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i];
                    if (mb == null) continue;

                    Type t = mb.GetType();
                    string typeName = t.Name != null ? t.Name.ToLower() : "";
                    if (!typeName.Contains("health"))
                        continue;

                    // team 필드에서 player 여부 판정 (있다면)
                    FieldInfo tf = GetTeamField(t);
                    bool isPlayerHealth = false;
                    if (tf != null)
                    {
                        string teamStr = GetTeamStringFromField(mb, tf);
                        string lowerTeam = string.IsNullOrEmpty(teamStr) ? "" : teamStr.ToLower();
                        if (lowerTeam.Contains("player"))
                            isPlayerHealth = true;
                    }

                    if (!isPlayerHealth)
                        continue;

                    float d = Vector3.Distance(playerTr.position, mb.transform.position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestMb = mb;
                    }
                }

                if (bestMb == null)
                {
                    Debug.Log("[EnemyRadarHUD] SetupPlayerHealthAccessor - player Health 후보를 찾지 못함");
                    return;
                }

                Type ht = bestMb.GetType();
                string typeNameFull = ht.FullName ?? ht.Name;

                // 1차: 필드에서 hp/health/life 이름 가진 숫자형 찾기 (스코어 기반)
                FieldInfo bestHpField = null;
                int bestScore = int.MinValue;
                FieldInfo[] fields = ht.GetFields(flags);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = fields[i];
                    Type ft = f.FieldType;

                    if (!(ft == typeof(int) || ft == typeof(float) || ft == typeof(double) ||
                          ft == typeof(long) || ft == typeof(short)))
                        continue;

                    string fname = f.Name != null ? f.Name.ToLower() : "";
                    if (!(fname.Contains("hp") || fname.Contains("health") || fname.Contains("life")))
                        continue;

                    int score = 0;
                    if (fname == "hp" || fname == "health") score += 30;
                    if (fname.Contains("cur") || fname.Contains("current") || fname.Contains("now")) score += 50;
                    if (fname.Contains("max")) score -= 40;
                    if (fname.Contains("hash") || fname.Contains("id") || fname.Contains("index")) score -= 60;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestHpField = f;
                    }
                }

                // 2차: 필드는 못 찾았는데 프로퍼티에서는 있는지
                if (bestHpField == null)
                {
                    PropertyInfo[] props = ht.GetProperties(flags);
                    for (int i = 0; i < props.Length; i++)
                    {
                        PropertyInfo p = props[i];
                        Type pt = p.PropertyType;
                        if (!(pt == typeof(int) || pt == typeof(float) || pt == typeof(double) ||
                              pt == typeof(long) || pt == typeof(short)))
                            continue;

                        string pname = p.Name != null ? p.Name.ToLower() : "";
                        if (pname.Contains("hp") || pname.Contains("health") || pname.Contains("life"))
                        {
                            _playerHealthMb = bestMb;
                            _playerCurHpField = null;
                            _playerMaxHpField = null;
                            _playerMaxHpProp = p;
                            _playerHpMaxObserved = 1f;

                            Debug.Log("[EnemyRadarHUD] 플레이어 Health 바인딩(프로퍼티): " + typeNameFull +
                                      " (prop=" + p.Name + ")");
                            return;
                        }
                    }
                }

                if (bestHpField != null)
                {
                    _playerHealthMb = bestMb;
                    _playerCurHpField = bestHpField;
                    _playerMaxHpField = null;
                    _playerMaxHpProp = null;
                    _playerHpMaxObserved = 1f;

                    Debug.Log("[EnemyRadarHUD] 플레이어 Health 바인딩(필드): " + typeNameFull +
                              " (field=" + bestHpField.Name + ")");
                    return;
                }

                Debug.Log("[EnemyRadarHUD] SetupPlayerHealthAccessor - hp/health 숫자 필드를 찾지 못함. type=" + typeNameFull);
            }
            catch (Exception ex)
            {
                Debug.Log("[EnemyRadarHUD] SetupPlayerHealthAccessor 예외: " + ex);
                _playerHealthMb = null;
                _playerCurHpField = null;
                _playerMaxHpField = null;
                _playerMaxHpProp = null;
            }
        }

        private void UpdatePlayerHealthRatio()
        {
            _hasHpRatio = false;
            _playerHpRatio = 1f;

            if (_playerHealthMb == null)
                return;

            try
            {
                float cur = 0f;
                bool ok = false;

                if (_playerCurHpField != null)
                {
                    object curObj = _playerCurHpField.GetValue(_playerHealthMb);
                    if (curObj != null)
                    {
                        cur = Convert.ToSingle(curObj);
                        ok = true;
                    }
                }
                else if (_playerMaxHpProp != null)
                {
                    object curObj = _playerMaxHpProp.GetValue(_playerHealthMb, null);
                    if (curObj != null)
                    {
                        cur = Convert.ToSingle(curObj);
                        ok = true;
                    }
                }

                if (!ok)
                    return;

                if (cur <= 0f)
                {
                    _playerHpRatio = 0f;
                    _hasHpRatio = true;
                    return;
                }

                // 처음엔 현재 값을 최대값으로 간주하고, 이후 더 큰 값 나오면 갱신
                if (_playerHpMaxObserved < 0.01f || cur > _playerHpMaxObserved)
                    _playerHpMaxObserved = cur;

                if (_playerHpMaxObserved <= 0.01f)
                    return;

                _playerHpRatio = Mathf.Clamp01(cur / _playerHpMaxObserved);
                _hasHpRatio = true;
            }
            catch (Exception ex)
            {
                Debug.Log("[EnemyRadarHUD] UpdatePlayerHealthRatio 예외: " + ex);
                _playerHealthMb = null;
                _playerCurHpField = null;
                _playerMaxHpField = null;
                _playerMaxHpProp = null;
            }
        }

// ================== 전리품 스캔 ==================

        private void ScanLootWorld()
        {
            _enemyLootSpots.Clear();
            ClearAllLootBeams();

            GameObject[] allGos = UnityEngine.Object.FindObjectsOfType<GameObject>();
            if (allGos == null || allGos.Length == 0)
            {
                Debug.Log("[EnemyRadarHUD] ScanLootWorld - GameObject 없음");
                return;
            }

            int lootCount = 0;

            for (int i = 0; i < allGos.Length; i++)
            {
                GameObject go = allGos[i];
                if (go == null) continue;

                string name = go.name;
                if (string.IsNullOrEmpty(name)) continue;

                string lowerName = name.ToLower();

                // ★ 적이 죽어서 떨어지는 전리품 가방만: LootBox_EnemyDie_Template(Clone)
                string Name = go.name;
                string LowerName = string.IsNullOrEmpty(name) ? string.Empty : name.ToLowerInvariant();

                // 리롤 모드와 같은 컨테이너 키워드 기준으로 필터
                bool nameMatch = false;
                for (int k = 0; k < _lootContainerKeywords.Length; k++)
                {
                    string kw = _lootContainerKeywords[k];
                    if (!string.IsNullOrEmpty(kw) && lowerName.Contains(kw))
                    {
                        nameMatch = true;
                        break;
                    }
                }

                // 키워드 아무 것도 안 맞으면 이 오브젝트는 루팅 컨테이너가 아님
                if (!nameMatch)
                    continue;


                Transform tr = go.transform;
                if (tr == null) continue;

                int bestQ = GetMaxQualityFromGameObject(go);

                LootSpot spot = new LootSpot();
                spot.Tr = tr;
                spot.Tier = bestQ;

                _enemyLootSpots.Add(spot);
                lootCount++;

                // 🔽 추가
                if (lootCount >= 40)
                    break;

                CreateLootBeamForSpot(spot);

                Debug.Log("[EnemyRadarHUD] LootSpot - name=" + name +
                          ", scene=" + go.scene.name +
                          ", bestQ=" + bestQ);
            }

            Debug.Log("[EnemyRadarHUD] ScanLootWorld - enemyLoot count=" + lootCount);
        }

        private int GetMaxQualityFromGameObject(GameObject go)
        {
            if (go == null) return 0;

            int best = 0;

            MonoBehaviour[] mbs = go.GetComponents<MonoBehaviour>();
            if (mbs == null || mbs.Length == 0)
                return 0;

            for (int i = 0; i < mbs.Length; i++)
            {
                MonoBehaviour mb = mbs[i];
                if (mb == null) continue;

                int q = GetMaxQualityFromComponent(mb);
                if (q > best) best = q;
            }

            return best;
        }

        private int GetMaxQualityFromComponent(MonoBehaviour mb)
        {
            if (mb == null) return 0;

            int best = 0;
            Type t = mb.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // 필드에서 Item / Item[] / List<Item> 전부 스캔
            FieldInfo[] fields = t.GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                Type ft = f.FieldType;

                try
                {
                    // 1) 단일 Item
                    if (ft == typeof(Item))
                    {
                        object obj = f.GetValue(mb);
                        Item item = obj as Item;
                        if (item != null)
                        {
                            int tier = GetLootTierIndex(item);
                            if (tier > best) best = tier;
                        }
                        continue;
                    }

                    // 2) Item 배열
                    if (ft.IsArray && ft.GetElementType() == typeof(Item))
                    {
                        object arrObj = f.GetValue(mb);
                        Array arr = arrObj as Array;
                        if (arr == null) continue;

                        foreach (object o in arr)
                        {
                            Item item = o as Item;
                            if (item == null) continue;
                            int tier = GetLootTierIndex(item);
                            if (tier > best) best = tier;
                        }
                        continue;
                    }

                    // 3) List<Item> 같은 컬렉션
                    if (typeof(IList).IsAssignableFrom(ft))
                    {
                        object listObj = f.GetValue(mb);
                        IList list = listObj as IList;
                        if (list == null || list.Count == 0) continue;

                        for (int idx = 0; idx < list.Count; idx++)
                        {
                            object elem = list[idx];
                            Item item = elem as Item;
                            if (item == null) continue;
                            int tier = GetLootTierIndex(item);
                            if (tier > best) best = tier;
                        }
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log("[EnemyRadarHUD] GetMaxQualityFromComponent 필드 예외: " +
                              t.Name + "." + f.Name + " - " + ex);
                }
            }

            return best;
        }

        // displayQuality → 0~6 등급 인덱스
        private int GetLootTierIndex(Item item)
        {
            if (item == null) return 0;

            int displayQuality = 0;

            try
            {
                Type t = item.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // 1) displayQuality 필드
                FieldInfo fDisplay = t.GetField("displayQuality", flags);
                if (fDisplay != null)
                {
                    object raw = fDisplay.GetValue(item);
                    if (raw != null)
                        displayQuality = Convert.ToInt32(raw);
                }

                // 2) displayQuality / DisplayQuality 프로퍼티
                if (displayQuality == 0)
                {
                    PropertyInfo pDisplay = t.GetProperty("displayQuality", flags);
                    if (pDisplay == null)
                        pDisplay = t.GetProperty("DisplayQuality", flags);

                    if (pDisplay != null)
                    {
                        object raw = pDisplay.GetValue(item, null);
                        if (raw != null)
                            displayQuality = Convert.ToInt32(raw);
                    }
                }

                // 3) quality 필드 (보조)
                if (displayQuality == 0)
                {
                    FieldInfo fQuality = t.GetField("quality", flags);
                    if (fQuality != null)
                    {
                        object raw = fQuality.GetValue(item);
                        if (raw != null)
                            displayQuality = Convert.ToInt32(raw);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[EnemyRadarHUD] GetLootTierIndex 예외: " + ex);
            }

            
            if (displayQuality < 0)
                displayQuality = 0;

            // Duckov 쪽이 0~9 품질을 쓰는 걸 가정:
            // - 9 이상  : 노랑(전설)  → 5번 (노랑)
            // - 7~8     : 빨강        → 6번 (빨강)
            // - 0~6     : 그대로 사용
            if (displayQuality >= 9)
                displayQuality = 5;
            else if (displayQuality > 6)
                displayQuality = 6;

            // 0 = 흰, 1=초록, 2=파랑, 3=보라, 4=금, 5=노랑, 6=빨강
            return displayQuality;
        }

        // loot 품질 숫자 -> 점 색
// bestQ(=tier)는 displayQuality 그대로 들어온다고 가정:
// 2=초록, 3=파랑, 4=보라, 5=금, 6=연빨, 7=진빨
private static Color GetLootColorByTier(int tier)
{
    // 0~1 이하는 "등급 없음"으로 회색 처리
    if (tier <= 1)
        return new Color(0.5f, 0.5f, 0.5f, 0.9f);

    switch (tier)
    {
        case 2: // 초록
            return new Color(0.3f, 1f, 0.3f, 0.95f);

        case 3: // 파랑
            return new Color(0.3f, 0.6f, 1f, 0.95f);

        case 4: // 보라
            return new Color(0.75f, 0.3f, 1f, 0.95f);

        case 5: // 금색
            return new Color(1f, 0.9f, 0.3f, 0.95f);

        case 6: // 연한 빨강
            return new Color(1f, 0.5f, 0.5f, 0.95f);

        default: // 7 이상 = 진빨
            return new Color(1f, 0.1f, 0.1f, 0.95f);
    }
}

// 디버그용 텍스트(로그에 tier 이름 찍을 때 사용)
private static string GetLootTierName(int tier)
{
    if (tier <= 1) return "등급없음";

    switch (tier)
    {
        case 2: return "초록(2)";
        case 3: return "파랑(3)";
        case 4: return "보라(4)";
        case 5: return "금색(5)";
        case 6: return "연빨(6)";
        default: return "진빨(7+)";
    }
}
        // HP 경고 문구 다국어 지원
        // HP 경고 문구 다국어 + 수동 토글
        // HP 경고 문구 – 시스템 언어에 따라 자동 선택 (한 줄)
        private string GetLowHpWarningText()
        {
            SystemLanguage lang = Application.systemLanguage;

            // 일본어
            if (lang == SystemLanguage.Japanese)
                return "バイタルサインが危険レベルです。";

            // 영어
            if (lang == SystemLanguage.English)
                return "Vital signs are at critical levels.";

            // 그 외(기본: 한국어)
            return "바이털 사인이 위험 수준입니다.";
        }




        // ================== 텍스처 / 스타일 빌드 ==================


        // ───── 전리품 빔 관리 ─────
        private void ClearAllLootBeams()
        {
            for (int i = 0; i < _lootBeams.Count; i++)
            {
                GameObject go = _lootBeams[i];
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }
            _lootBeams.Clear();
        }

        private void CreateLootBeamForSpot(LootSpot spot)
        {
            if (spot == null || spot.Tr == null)
                return;
            if (_lootBeamMaterial == null)
                return;

            // 너무 낮은 등급(흰/초록/파랑)은 빔 생략
            if (spot.Tier <= 2)
                return;

            GameObject beam = new GameObject("EnemyLootBeam");
            if (_lootBeamRoot != null)
            {
                beam.transform.SetParent(_lootBeamRoot.transform, false);
            }

            Vector3 basePos = spot.Tr.position;
            basePos.y += _lootBeamOffsetY;
            Vector3 topPos = basePos + Vector3.up * _lootBeamHeight;

            LineRenderer lr = beam.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.SetPosition(0, basePos);
            lr.SetPosition(1, topPos);

            lr.startWidth = _lootBeamWidth;
            lr.endWidth   = _lootBeamWidth;

            lr.material = _lootBeamMaterial;

            Color c = GetLootColorByTier(spot.Tier);
            c.a = 0.8f;
            lr.startColor = c;
            lr.endColor   = c;

            _lootBeams.Add(beam);
        }

        private void OnDestroy()
        {
            ClearAllLootBeams();
        }


private void BuildTextures()
        {
            int size = 256;

            _radarTexture = BuildRadarBackground(size);

            Color red = new Color(1f, 0.1f, 0.1f, 0.95f);

            // 2번 링: 빨간 도넛 "원형 링"
            _ring2Texture = BuildSolidRingTexture(
                size,
                0.20f, 0.38f,
                red);

            // 3번 링: 방향 실선 피자조각 (원호 일부만 채움)
            _ring3Texture = BuildSolidArcTexture(
                size,
                0.50f, 0.56f,
                40f,
                red);

            // 4번 링: 바깥 점선 아크
            _ring4Texture = BuildSegmentedArcTexture(
                size,
                0.80f, 0.88f,
                45f,
                9,
                0.65f,
                red);

            // 전리품 점용 동그라미 텍스처 (흰색, 색은 GUI.color 로 입힘)
            _lootDotTexture = BuildDotTexture(32);

            _texReady = true;
        }

        private void BuildStyle()
        {
            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 18;
            _labelStyle.normal.textColor = Color.white;
            _labelStyle.alignment = TextAnchor.UpperLeft;

            _lowHpStyle = new GUIStyle(GUI.skin.label);
            _lowHpStyle.fontSize = 18;
            _lowHpStyle.fontStyle = FontStyle.Bold;
            _lowHpStyle.alignment = TextAnchor.MiddleCenter;
            // HP 경고 문구 색 (노란색)
            _lowHpStyle.normal.textColor = new Color(1f, 0.9f, 0.2f, 1f);

            _styleReady = true;
        }


        private Texture2D BuildRadarBackground(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;

            float outerRadius   = size * 0.48f;
            float outerRadiusSq = outerRadius * outerRadius;

            Color bgInner  = new Color(0.9f, 0.9f, 0.9f, 0.03f);
            Color bgOuter  = new Color(0f, 0f, 0f, 0.55f);
            Color ringEdge = new Color(1f, 0.6f, 0.1f, 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;

                    if (distSq > outerRadiusSq)
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }

                    float dist = Mathf.Sqrt(distSq);
                    float t = dist / outerRadius;

                    Color c = Color.Lerp(bgInner, bgOuter, t * t);

                    float borderWidth = 2f;
                    if (Mathf.Abs(dist - outerRadius) <= borderWidth)
                    {
                        c = ringEdge;
                    }

                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply();
            return tex;
        }

        // 2번 링용: 전체 "도넛 링"
        private Texture2D BuildSolidRingTexture(
            int size,
            float innerRatio,
            float outerRatio,
            Color color)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;

            float radarOuter = size * 0.48f;
            float innerR = radarOuter * innerRatio;
            float outerR = radarOuter * outerRatio;

            float innerSq = innerR * innerR;
            float outerSq = outerR * outerR;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;

                    if (distSq >= innerSq && distSq <= outerSq)
                    {
                        tex.SetPixel(x, y, color);
                    }
                    else
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                    }
                }
            }

            tex.Apply();
            return tex;
        }

        // 3번 링용: 실선 피자조각 아크
        private Texture2D BuildSolidArcTexture(
            int size,
            float innerRatio,
            float outerRatio,
            float halfAngleDeg,
            Color color)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;

            float radarOuter = size * 0.48f;
            float innerR = radarOuter * innerRatio;
            float outerR = radarOuter * outerRatio;

            float innerSq = innerR * innerR;
            float outerSq = outerR * outerR;

            float centerAngle = 90f; // 위쪽(12시 방향)

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;

                    if (distSq < innerSq || distSq > outerSq)
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }

                    float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                    if (angle < 0f) angle += 360f;

                    float delta = Mathf.DeltaAngle(angle, centerAngle);

                    // halfAngleDeg 안쪽만 칠해서 "조각" 모양
                    if (Mathf.Abs(delta) <= halfAngleDeg)
                        tex.SetPixel(x, y, color);
                    else
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                }
            }

            tex.Apply();
            return tex;
        }

        // 4번 링용: 점선 반원 아크
        private Texture2D BuildSegmentedArcTexture(
            int size,
            float innerRatio,
            float outerRatio,
            float halfAngleDeg,
            int segmentCount,
            float fillRatio,
            Color color)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;

            float radarOuter = size * 0.48f;
            float innerR = radarOuter * innerRatio;
            float outerR = radarOuter * outerRatio;

            float innerSq = innerR * innerR;
            float outerSq = outerR * outerR;

            float centerAngle = 90f; // 위쪽(12시 방향)
            float totalAngle  = halfAngleDeg * 2f;

            if (segmentCount <= 0) segmentCount = 1;
            if (fillRatio   <= 0f) fillRatio   = 0.01f;
            if (fillRatio   >  1f) fillRatio   = 1f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;

                    if (distSq < innerSq || distSq > outerSq)
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }

                    float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                    if (angle < 0f) angle += 360f;

                    float delta = Mathf.DeltaAngle(angle, centerAngle);
                    if (Mathf.Abs(delta) > halfAngleDeg)
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }

                    // [-halfAngle, +halfAngle] -> [0, 1]
                    float t = (delta + halfAngleDeg) / totalAngle;

                    float scaled = t * segmentCount;
                    int segIdx = Mathf.FloorToInt(scaled);
                    if (segIdx >= segmentCount) segIdx = segmentCount - 1;

                    float inSeg = scaled - segIdx; // 0~1

                    if (inSeg <= fillRatio)
                        tex.SetPixel(x, y, color);
                    else
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                }
            }

            tex.Apply();
            return tex;
        }

        // 전리품 점용 동그라미 텍스처 (흰색)
        private Texture2D BuildDotTexture(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;
            float r  = size * 0.45f;
            float rSq = r * r;

            Color cInner = Color.white;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;

                    if (distSq <= rSq)
                        tex.SetPixel(x, y, cInner);
                    else
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                }
            }

            tex.Apply();
            return tex;
        }
    }
}
