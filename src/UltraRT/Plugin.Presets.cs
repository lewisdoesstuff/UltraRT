using System;
using UnityEngine;

namespace UltraRT
{
    public enum QualityPreset
    {
        Ultra = 0,
        High = 1,
        Performance = 2,
        Competitive = 3,
        Custom = 4,
    }

    public partial class Plugin
    {
        private QualityPreset _cliPreset = QualityPreset.Custom;
        private bool _cliPresetSet;

        private void ParsePresetArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], "-ultrart-preset", StringComparison.OrdinalIgnoreCase) || i + 1 >= a.Length) continue;
                switch ((a[++i] ?? "").ToLowerInvariant())
                {
                    case "ultra": _cliPreset = QualityPreset.Ultra; _cliPresetSet = true; break;
                    case "high": case "default": _cliPreset = QualityPreset.High; _cliPresetSet = true; break;
                    case "performance": case "perf": _cliPreset = QualityPreset.Performance; _cliPresetSet = true; break;
                    case "competitive": case "comp": case "maxfps": _cliPreset = QualityPreset.Competitive; _cliPresetSet = true; break;
                }
            }
            if (_cliPresetSet)
            {
                ApplyQualityPreset(_cliPreset);
                Logger.LogInfo($"Quality preset applied from CLI: {_cliPreset}");
            }
        }

        internal QualityPreset DetectQualityPreset()
        {
            if (Mathf.Abs(_scale - 0.67f) < 0.02f && Mathf.Abs(_ptSettings.scaleRR - 0.5f) < 0.02f &&
                _ptSettings.bounces == 3 && !_ptSettings.singleLobe && _volSettings.enabled && _volSettings.block == 4 &&
                _volSettings.steps == 8 && _particleSettings.enabled && _fxgSettings.enabled)
                return QualityPreset.Ultra;

            if (Mathf.Abs(_scale - 0.5f) < 0.02f && Mathf.Abs(_ptSettings.scaleRR - 0.3333f) < 0.02f &&
                _ptSettings.bounces == 2 && !_ptSettings.singleLobe && _volSettings.enabled && _volSettings.block == 4 &&
                _volSettings.steps == 8 && _particleSettings.enabled && _fxgSettings.enabled)
                return QualityPreset.High;

            if (Mathf.Abs(_scale - 0.5f) < 0.02f && Mathf.Abs(_ptSettings.scaleRR - 0.3333f) < 0.02f &&
                _ptSettings.bounces == 1 && _ptSettings.singleLobe && _volSettings.enabled && _volSettings.block == 6 &&
                _volSettings.steps == 4 && _particleSettings.enabled && _fxgSettings.enabled)
                return QualityPreset.Performance;

            if (Mathf.Abs(_scale - 0.5f) < 0.02f && Mathf.Abs(_ptSettings.scaleRR - 0.3333f) < 0.02f &&
                _ptSettings.bounces == 1 && _ptSettings.singleLobe && !_volSettings.enabled &&
                !_particleSettings.enabled && !_fxgSettings.enabled)
                return QualityPreset.Competitive;

            return QualityPreset.Custom;
        }

        internal void ApplyQualityPreset(QualityPreset preset)
        {
            switch (preset)
            {
                case QualityPreset.Ultra:
                    _scale = 0.67f;
                    _ptSettings.scaleRR = 0.5f;
                    _ptSettings.scale = 0.5f;
                    _ptSettings.paths = 1;
                    _ptSettings.bounces = 3;
                    _ptSettings.singleLobe = false;
                    _volSettings.enabled = true;
                    _volSettings.block = 4;
                    _volSettings.steps = 8;
                    _vmEnabled = true;
                    _particleSettings.enabled = true;
                    _fxgSettings.enabled = true;
                    break;
                case QualityPreset.High:
                    _scale = 0.5f;
                    _ptSettings.scaleRR = 0.3333f;
                    _ptSettings.scale = 0.3333f;
                    _ptSettings.paths = 1;
                    _ptSettings.bounces = 2;
                    _ptSettings.singleLobe = false;
                    _volSettings.enabled = true;
                    _volSettings.block = 4;
                    _volSettings.steps = 8;
                    _vmEnabled = true;
                    _particleSettings.enabled = true;
                    _fxgSettings.enabled = true;
                    break;
                case QualityPreset.Performance:
                    _scale = 0.5f;
                    _ptSettings.scaleRR = 0.3333f;
                    _ptSettings.scale = 0.3333f;
                    _ptSettings.paths = 1;
                    _ptSettings.bounces = 1;
                    _ptSettings.singleLobe = true;
                    _volSettings.enabled = true;
                    _volSettings.block = 6;
                    _volSettings.steps = 4;
                    _vmEnabled = true;
                    _particleSettings.enabled = true;
                    _fxgSettings.enabled = true;
                    break;
                case QualityPreset.Competitive:
                    _scale = 0.5f;
                    _ptSettings.scaleRR = 0.3333f;
                    _ptSettings.scale = 0.3333f;
                    _ptSettings.paths = 1;
                    _ptSettings.bounces = 1;
                    _ptSettings.singleLobe = true;
                    _volSettings.enabled = false;
                    _vmEnabled = true;
                    _particleSettings.enabled = false;
                    _fxgSettings.enabled = false;
                    break;
                default:
                    return;
            }
            _denoiser?.Reset();
            _lightSampling?.Reset();
            _reflFilter?.Reset();
            _volumetrics?.Reset();
            _particleLight?.Reset();
            _vmHistValid = false;
        }
    }
}
