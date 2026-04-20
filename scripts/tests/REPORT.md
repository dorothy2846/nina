# Driver verification report

Generated from 200 driver fixture(s) captured via `driver_smoke_test.py --fixture-dir`.

## Installation health

- Drivers bundled: 200 (188 vendor, 12 simulator)
- Every listed driver launched + spoke INDI protocol successfully (else it would be missing from the fixture set).

## Device-kind coverage (from DRIVER_INTERFACE)

Counts drivers whose bitmask advertises each kind. Multi-kind drivers (e.g. powerboxes = FOCUSER + AUX + DUSTCAP) are counted once per kind.

| Kind | Drivers |
|---|---|
| TELESCOPE | 42 |
| CAMERA | 2 |
| GUIDER | 36 |
| FOCUSER | 65 |
| FILTER | 11 |
| DOME | 12 |
| GPS | 1 |
| WEATHER | 21 |
| DUSTCAP | 10 |
| LIGHTBOX | 12 |
| DETECTOR | 14 |
| SPECTROGRAPH | 1 |
| AUX | 45 |

## Simulator capability probe results

Simulator drivers publish their full capability property set pre-connection (no hardware to wait on), so these are the only fixtures where offline capability probing produces real signal. If a probe below says `—` for a simulator that obviously should support the feature, that's a regression in either the simulator or our probe names.

| Driver | canAbort | hasDewHeater | hasOffset | hasGain | canSetTemperature | canSetTrackRate | canSync | canPark | canFindHome | canSetTracking | canPulseGuide | hasTemperature | hasTempCompensation | hasBacklash | hasFlatLight | hasFlatIntensity |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| indi_simulator_ccd | ✓ | — | ✓ | ✓ | ✓ | — | — | — | — | — | ✓ | — | — | — | — | — |
| indi_simulator_dome | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — |
| indi_simulator_dustcover | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — |
| indi_simulator_focus | — | — | — | — | — | — | — | — | — | — | — | ✓ | — | ✓ | — | — |
| indi_simulator_gps | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — |
| indi_simulator_guide | ✓ | — | — | ✓ | ✓ | — | — | — | — | — | ✓ | — | — | — | — | — |
| indi_simulator_io | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — |
| indi_simulator_lightpanel | — | — | — | — | — | — | — | — | — | — | — | — | — | — | ✓ | ✓ |
| indi_simulator_pac | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — |
| indi_simulator_receiver | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — |
| indi_simulator_rotator | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — |
| indi_simulator_sqm | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — |

## Per-driver kind classification (vendor drivers)

For real drivers the pre-connection property set is generic (CONNECTION / DRIVER_INFO / POLLING_PERIOD / …) — capability-specific properties only show up after hardware connect. Real-gear capability regressions are caught via (a) simulator fixtures above, (b) user-submitted diagnostic dumps (/api/v1/diagnostics), or (c) live hardware tests.

### AUX (19 drivers)

- indi_astrometry                          - indi_sqm_weather
- indi_avalon_upas                         - indi_startech_hub
- indi_cheapodc                            - indi_svbony_powerbox
- indi_ipx800v4                            - indi_terrans_powerboxgo_v2
- indi_meta_weather                        - indi_ups_safety
- indi_mlastro_rpa                         - indi_usbdewpoint
- indi_mydcp4esp32                         - indi_wanderer_dew_terminator
- indi_planewave_deltat                    - indi_watchdog
- indi_safetymonitor                       - indi_wavesharemodbus_relay
- indi_skysafari

### CAMERA (2 drivers)

- indi_alpaca_ccd                          - indi_seestar_ccd

### DETECTOR (9 drivers)

- indi_camelot_rotator                     - indi_pyxis_rotator
- indi_deepskydad_fr1                      - indi_wanderer_lite_rotator
- indi_falcon_rotator                      - indi_wanderer_rotator_lite_v2
- indi_falconv2_rotator                    - indi_wanderer_rotator_mini
- indi_nframe_rotator

### DOME (12 drivers)

- indi_alpaca_dome                         - indi_nexdome_beaver
- indi_baader_dome                         - indi_rigel_dome
- indi_ddw_dome                            - indi_rolloff_dome
- indi_domepro2_dome                       - indi_scopedome_dome
- indi_dragonlair_dome                     - indi_script_dome
- indi_hakos_roof                          - indi_universalror_dome

### DUSTCAP (9 drivers)

- indi_Excalibur                           - indi_wanderer_cover
- indi_alto                                - indi_wanderer_eclipse
- indi_astrolink4                          - indi_wanderercover_v4_ec
- indi_deepskydad_fp                       - indi_wanderercover_v4_pro_ec
- indi_snapcap

### FILTER (11 drivers)

- indi_alpaca_filterwheel                  - indi_qhycfw2_wheel
- indi_ioptron_wheel                       - indi_qhycfw3_wheel
- indi_manual_wheel                        - indi_quantum_wheel
- indi_optec_wheel                         - indi_trutech_wheel
- indi_pegasusindigo_wheel                 - indi_xagyl_wheel
- indi_qhycfw1_wheel

### FOCUSER (54 drivers)

- indi_aaf2_focus                          - indi_moonlitedro_focus
- indi_activefocuser_focus                 - indi_myfocuserpro2_focus
- indi_alluna_tcs2                         - indi_nfocus
- indi_alpaca_focuser                      - indi_nightcrawler_focus
- indi_astrolink4mini2                     - indi_nstep_focus
- indi_astromechfoc                        - indi_onfocus_focus
- indi_celestron_sct_focus                 - indi_pegasus_focuscube
- indi_deepskydad_af1_focus                - indi_pegasus_focuscube3
- indi_deepskydad_af2_focus                - indi_pegasus_prodigyMF
- indi_deepskydad_af3_focus                - indi_pegasus_scopsoag
- indi_dmfc_focus                          - indi_pegasus_upb
- indi_dreamfocuser_focus                  - indi_pegasus_upb3
- indi_efa_focus                           - indi_perfectstar_focus
- indi_esatto_focus                        - indi_pinefeat_cef_focus
- indi_esattoarco_focus                    - indi_rainbowrsf_focus
- indi_fcusb_focus                         - indi_rbfocus_focus
- indi_gemini_focus                        - indi_robo_focus
- indi_hitecastrodc_focus                  - indi_sestosenso2_focus
- indi_iafscaa_focus                       - indi_sestosenso_focus
- indi_ieaf_focus                          - indi_siefs_focus
- indi_integra_focus                       - indi_smartfocus_focus
- indi_lacerta_mfoc_fmc_focus              - indi_steeldrive2_focus
- indi_lacerta_mfoc_focus                  - indi_steeldrive_focus
- indi_lakeside_focus                      - indi_tcfs3_focus
- indi_lynx_focus                          - indi_tcfs_focus
- indi_microtouch_focus                    - indi_teenastro_focus
- indi_moonlite_focus                      - indi_usbfocusv3_focus

### GPS (1 drivers)

- indi_uranus_weather

### GUIDER (2 drivers)

- indi_arduinost4                          - indi_gpusb

### LIGHTBOX (5 drivers)

- indi_dragon_light                        - indi_giotto
- indi_flipflat                            - indi_pegasus_flatmaster
- indi_gemini_flatpanel

### SPECTROGRAPH (1 drivers)

- indi_spectracyber

### TELESCOPE (42 drivers)

- indi_alpaca_telescope                    - indi_lx200basic
- indi_astrotrac_telescope                 - indi_lx200classic
- indi_celestron_gps                       - indi_lx200fs2
- indi_crux_mount                          - indi_lx200gemini
- indi_dsc_telescope                       - indi_lx200generic
- indi_eq500x_telescope                    - indi_lx200gotonova
- indi_ieq_telescope                       - indi_lx200gps
- indi_ieqlegacy_telescope                 - indi_lx200pulsar2
- indi_ioptronHC8406                       - indi_lx200ss2000pc
- indi_ioptronv3_telescope                 - indi_lx200zeq25
- indi_lx200_10micron                      - indi_paramount_telescope
- indi_lx200_16                            - indi_planewave_telescope
- indi_lx200_OnStep                        - indi_pmc8_telescope
- indi_lx200_OpenAstroTech                 - indi_rainbow_telescope
- indi_lx200_TeenAstro                     - indi_script_telescope
- indi_lx200_esp32go                       - indi_skycommander_telescope
- indi_lx200_pegasus_nyx101                - indi_skywatcherAltAzMount
- indi_lx200am5                            - indi_star2000
- indi_lx200ap_legacy                      - indi_synscan_telescope
- indi_lx200ap_v2                          - indi_synscanlegacy_telescope
- indi_lx200autostar                       - indi_temma_telescope

### UNCLASSIFIED (4 drivers)

- indi_alpaca_server                       - indi_imager_agent
- indi_astromech_lpm                       - indi_wake_on_lan

### WEATHER (17 drivers)

- indi_aagsolo_weather                     - indi_terrans_powerboxpro_v2
- indi_celestron_dewpower                  - indi_vantage_weather
- indi_hitech_weather                      - indi_wandererbox_plus_v3
- indi_mbox_weather                        - indi_wandererbox_pro_v3
- indi_myDewControllerPro                  - indi_watcher_weather
- indi_openweathermap_weather              - indi_weather_safety_alpaca
- indi_pegasus_ppb                         - indi_weather_safety_proxy
- indi_pegasus_ppba                        - indi_weatherflow_weather
- indi_pegasus_spb

