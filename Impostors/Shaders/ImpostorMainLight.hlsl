#ifndef IMPOSTOR_MAIN_LIGHT
#define IMPOSTOR_MAIN_LIGHT
// Set from URP's selected main light, independently of shadow-map availability or distance.
float _ImpostorMainLightShadowStrength;
float ImpostorMainLightShadowStrength() { return saturate(_ImpostorMainLightShadowStrength); }
#endif
