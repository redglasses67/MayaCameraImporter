﻿using UnityEngine;
using UnityEditor;

using System.IO;
using System.Linq;
using System.Collections.Generic;

using Object = UnityEngine.Object;

public class MayaCameraImporter : AssetPostprocessor
{
	void OnPreprocessModel()
	{
		var fileName = Path.GetFileNameWithoutExtension(assetPath);

		if (fileName.StartsWith("mCam_") == false) { return; }

		var modelImporter = assetImporter as ModelImporter;
		modelImporter.importAnimation = true;

		// VirtualCameraはGenericじゃないとだめなので変更しておく
		if (fileName.EndsWith("_VC") == true)
		{
			modelImporter.animationType = ModelImporterAnimationType.Generic;
		}
	}

	void OnPostprocessModel( GameObject importedModel )
	{
		var fileName = Path.GetFileNameWithoutExtension(assetPath);
		Debug.Log("OnPostprocessModel        fileName = " + fileName);
		if (fileName.StartsWith("mCam_") == false) { return; }

		var modelImporter = assetImporter as ModelImporter;
		ExtractCameraAnimClip(ref modelImporter, assetPath);
	}


	/// <summary>
	/// Mayaで制作したCameraのAnimationClipのコピーを取り出し,設定する
	/// </summary>
	/// <param name="importedModel">Imported model.</param>
	private static void ExtractCameraAnimClip(ref ModelImporter modelImporter, string assetPath)
	{
		var exportPath = Path.GetDirectoryName(assetPath);

		Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);

		// AnimationClipで且つ,名前に__preview__がついていないものをリストにする
		var clips = assets.ToList()
							.Where(x => x is AnimationClip && !x.name.Contains("__preview__"))
							.Cast<AnimationClip>()
							.ToList();

		if (clips == null || clips.Any() == false)
		{
			Debug.LogWarning("AnimationClipが取得できませんでした");
			modelImporter.SaveAndReimport();
			return;
		}

		var isResampleCurves = modelImporter.resampleCurves;

		foreach (AnimationClip clip in clips)
		{
			var fileName = Path.GetFileNameWithoutExtension(assetPath);

			string exportFullPath;
			if (fileName.Contains(clip.name) == true)
			{
				exportFullPath = exportPath + "/" + fileName + ".anim";
			}
			else
			{
				exportFullPath = exportPath + "/" + fileName + "_" + clip.name + ".anim";
			}
			Debug.Log("exportFullPath = " + exportFullPath);

			var clone = (AnimationClip)AssetDatabase.LoadAssetAtPath(exportFullPath, typeof(AnimationClip) );

			// すでに存在した場合はカーブ情報を一旦削除しておく
			if (File.Exists( exportFullPath ) == true && clone != null)
			{
				clone.ClearCurves();
			}
			else
			{
				clone = new AnimationClip();
			}

			clone.name      = clip.name;
			clone.legacy    = clip.legacy;
			clone.frameRate = clip.frameRate;

			AnimationEvent[] events;
			AnimationClipSettings srcClipInfo;

			clone.wrapMode = clip.wrapMode;
			events         = AnimationUtility.GetAnimationEvents(clip);
			srcClipInfo    = AnimationUtility.GetAnimationClipSettings(clip);
			AnimationUtility.SetAnimationEvents(clone, events);
			AnimationUtility.SetAnimationClipSettings(clone, srcClipInfo);

			var targetType = typeof(Camera);
#if UNITY_2018_1_OR_NEWER
			// ファイル名の末尾に_VCが付いていたら、VirtualCameraをターゲットとする
			if (fileName.EndsWith("_VC") == true)
			{
				targetType = typeof(Cinemachine.CinemachineVirtualCamera);
			}
#endif

			foreach (var binding in AnimationUtility.GetCurveBindings(clip))
			{
				var propName  = binding.propertyName;
				var animCurve = AnimationUtility.GetEditorCurve(clip, binding);

				// Scaleに格納してある Near・Far Clip Plane と FoV のアニメーションをCameraのプロパティに変換
				// ※AnimatorがCameraと同じオブジェクトに付いている想定の設定なので、
				//  そうでない場合はSetCurveの第一引数にAnimatorからCameraまでの相対パスを設定する必要があります。
				switch(propName)
				{
					case "m_LocalScale.x":
						if (fileName.EndsWith("_VC") == true && targetType != typeof(Camera))
						{
							clone.SetCurve("", targetType, "m_Lens.NearClipPlane", animCurve);
						}
						else
						{
							clone.SetCurve("", targetType, "near clip plane", animCurve);
						}
						break;

					case "m_LocalScale.y":
						if (fileName.EndsWith("_VC") == true && targetType != typeof(Camera))
						{
							clone.SetCurve("", targetType, "m_Lens.FarClipPlane", animCurve);
						}
						else
						{
							clone.SetCurve("", targetType, "far clip plane", animCurve);
						}
						break;

					case "m_LocalScale.z":
						if (fileName.EndsWith("_VC") == true && targetType != typeof(Camera))
						{
							clone.SetCurve("", targetType, "m_Lens.FieldOfView", animCurve);
						}
						else
						{
							clone.SetCurve("", targetType, "field of view", animCurve);
						}
						break;

					default:
						clone.SetCurve("", typeof(Transform), propName, animCurve);
						break;
				}
			}

			AdjustMayaCameraRotation(ref clone, isResampleCurves);

			// 圧縮がOFFの場合はTangentを変更しない。
			if (modelImporter.animationCompression != ModelImporterAnimationCompression.Off)
			{
				SetAllKeyBothTangentLinear(ref clone);
			}

			//すでに同名ファイルが存在している場合一時ファイルとして作成してその情報をコピーする.
			if (File.Exists(exportFullPath) == true)
			{
				EditorUtility.SetDirty( clone );
				AssetDatabase.SaveAssets();
				Debug.Log("Maya Camera [<b>" + fileName + "</b>] アニメデータを置き換えました : " + exportFullPath, clone);
			}
			else
			{
				AssetDatabase.CreateAsset(clone, exportFullPath);
				Debug.Log("Maya Camera [<b>" + fileName + "</b>] アニメデータを新規で作りました : " + exportFullPath, clone);
			}
		}
		
	}


	/// <summary>
	/// MayaのカメラアニメーションをUnityに持ってきた場合にY軸180度回転しないと合わないので回転した状態にしておく
	/// </summary>
	/// <param name="clip"></param>/
	private static void AdjustMayaCameraRotation(ref AnimationClip clip, bool isResampleCurves)
	{
		var frameValDict = new Dictionary<float, float[]>();
		var bindingArray = AnimationUtility.GetCurveBindings(clip);

		// 一度curve bindingsをforeachで回して、timeをKey・Rotationの値（Quaternion）をValueとした辞書に入れていく
		foreach (var binding in bindingArray)
		{
			var propName = binding.propertyName;

			// Rotation以外はパス
			if ((isResampleCurves == true && propName.Contains("m_LocalRotation") == false)
			||  (isResampleCurves == false && propName.Contains("localEulerAnglesRaw") == false))
			{
				continue;
			}

			var curve = AnimationUtility.GetEditorCurve(clip, binding);

			if (curve == null) { continue; }

			var keyArray = curve.keys;

			for (var i = 0; i < keyArray.Length; i++)
			{
				// まだ辞書に存在しないtimeのKeyだった場合、valueとして配列を設定
				if (frameValDict.ContainsKey(keyArray[i].time) == false)
				{
					frameValDict[keyArray[i].time] = new float[4];
				}

				if (propName.Contains(".x") == true)
				{
					frameValDict[keyArray[i].time][0] = keyArray[i].value;
				}
				else if (propName.Contains(".y") == true)
				{
					frameValDict[keyArray[i].time][1] = keyArray[i].value;
				}
				else if (propName.Contains(".z") == true)
				{
					frameValDict[keyArray[i].time][2] = keyArray[i].value;
				}
				else if (propName.Contains(".w") == true)
				{
					frameValDict[keyArray[i].time][3] = keyArray[i].value;
				}
			}
		}

		// 各フレームで元の値からY軸180度回転させた状態の新しい辞書を用意する
		var newFrameValDict = new Dictionary<float, float[]>();
		foreach (var frameVal in frameValDict)
		{
			if (isResampleCurves == true)
			{
				var rotQua = new Quaternion(frameVal.Value[0], frameVal.Value[1], frameVal.Value[2], frameVal.Value[3]);

				// Y軸を180度まわす
				var newRotQua = rotQua * Quaternion.Euler(0f, 180f, 0f);

				newFrameValDict[frameVal.Key] = new float[]{newRotQua.x, newRotQua.y, newRotQua.z, newRotQua.w};
			}
			else
			{
				var rotEuler = new Vector3(frameVal.Value[0], frameVal.Value[1], frameVal.Value[2]);

				// Y軸を180度まわす
				var newRotEuler = rotEuler + new Vector3(0f, 180f, 0f);
				if (newRotEuler.y >= 360)
				{
					newRotEuler.y -= 360;
				}

				newFrameValDict[frameVal.Key] = new float[]{newRotEuler.x, newRotEuler.y, newRotEuler.z, 0f};
			}
		}

		// 再度curve bindingsをforeachで回して、回転済みの新しいRotation値をSetEditorCurveしていく
		foreach (var binding in bindingArray)//AnimationUtility.GetCurveBindings(clip))
		{
			var propName = binding.propertyName;

			var curve = AnimationUtility.GetEditorCurve(clip, binding);

			var isRotProp = false;
			// Rotation以外はパス
			if ((isResampleCurves == true && propName.Contains("m_LocalRotation") == true)
			||  (isResampleCurves == false && propName.Contains("localEulerAnglesRaw") == true))
			{
				isRotProp = true;
			}

			if (isRotProp == true)
			{
				var animCurveLength  = curve.keys.Length;
				var adaptingKeyArray = new Keyframe[animCurveLength];

				for (var i = 0; i < animCurveLength; i++)
				{
					adaptingKeyArray[i] = curve.keys[i];

					if (propName.Contains(".x") == true)
					{
						adaptingKeyArray[i].value = newFrameValDict[curve.keys[i].time][0];
					}
					else if (propName.Contains(".y") == true)
					{
						adaptingKeyArray[i].value = newFrameValDict[curve.keys[i].time][1];
					}
					else if (propName.Contains(".z") == true)
					{
						adaptingKeyArray[i].value = newFrameValDict[curve.keys[i].time][2];
					}
					else if (propName.Contains(".w") == true)
					{
						adaptingKeyArray[i].value = newFrameValDict[curve.keys[i].time][3];
					}
				}
				curve.keys = adaptingKeyArray;
			}
			AnimationUtility.SetEditorCurve(clip, binding, curve);
		}
	}


	private static void SetAllKeyBothTangentLinear(ref AnimationClip clip)
	{
		var so_clip = new SerializedObject(clip);

		string[] serializedCurveNameArray = 
		{
			"m_EditorCurves",
			"m_EulerEditorCurves",
			"m_PositionCurves",
			"m_RotationCurves",
			"m_ScaleCurves",
			"m_FloatCurves" 
		};
		var sampleRate      = so_clip.FindProperty("m_SampleRate").floatValue;
		var oneKeyframeTime = (float)((int)((1.0f / sampleRate) * 1000)) / 1000 + 0.001f;
		Debug.Log("oneKeyframeTime = " + oneKeyframeTime);
		foreach (var serializedCurveName in serializedCurveNameArray)
		{
			var sp_curveArray = so_clip.FindProperty(serializedCurveName);
			if (sp_curveArray == null) { continue; }

			var sp_curveArrayLength = sp_curveArray.arraySize;
			if (sp_curveArrayLength == 0) { continue; }

			for (var i = 0; i < sp_curveArrayLength; i++)
			{
				var sp_curveInfo            = sp_curveArray.GetArrayElementAtIndex(i);
				var sp_curve                = sp_curveInfo.FindPropertyRelative("curve");
				var sp_curveDataArray       = sp_curve.FindPropertyRelative("m_Curve");
				var sp_curveDataArrayLength = sp_curveDataArray.arraySize;
				if (sp_curveDataArrayLength == 0) { continue; }

				// 最後のkeyframeのoutslopeは不要なので, sp_curveDataArrayLength - 1
				for (var h = 0; h < sp_curveDataArrayLength - 1; h++)
				{
					var keyframe1 = sp_curveDataArray.GetArrayElementAtIndex(h);
					var keyframe2 = sp_curveDataArray.GetArrayElementAtIndex(h + 1);

					var val1      = keyframe1.FindPropertyRelative("value");
					var time1     = keyframe1.FindPropertyRelative("time");
					var outSlope1 = keyframe1.FindPropertyRelative("outSlope");

					var val2      = keyframe2.FindPropertyRelative("value");
					var time2     = keyframe2.FindPropertyRelative("time");
					var inSlope2  = keyframe2.FindPropertyRelative("inSlope");

					var sp_attr = sp_curveInfo.FindPropertyRelative("attribute");
					var attr    = (sp_attr != null) ? sp_attr.stringValue : "";
					attr        += "  count = " + h;
					
					// Debug.Log("");
					switch (val1.propertyType)
					{
						case SerializedPropertyType.Float:
							IsLinearTargetTangent(
								"Float - " + attr,
								val1.floatValue,
								outSlope1.floatValue,
								time1.floatValue,
								val2.floatValue,
								inSlope2.floatValue,
								time2.floatValue,
								oneKeyframeTime);
							var tan = CalculateLinearTangent(
											val1.floatValue,
											time1.floatValue,
											val2.floatValue,
											time2.floatValue);
							outSlope1.floatValue = tan;
							inSlope2.floatValue  = tan;
							break;

						case SerializedPropertyType.Vector3:
							IsLinearTargetTangent(
								"Vector3 - X - " + attr,
								val1.vector3Value.x,
								outSlope1.vector3Value.x,
								time1.floatValue,
								val2.vector3Value.x,
								inSlope2.vector3Value.x,
								time2.floatValue,
								oneKeyframeTime);
							IsLinearTargetTangent(
								"Vector3 - Y - " + attr,
								val1.vector3Value.y,
								outSlope1.vector3Value.y,
								time1.floatValue,
								val2.vector3Value.y,
								inSlope2.vector3Value.y,
								time2.floatValue,
								oneKeyframeTime);
							IsLinearTargetTangent(
								"Vector3 - Z - " + attr,
								val1.vector3Value.z,
								outSlope1.vector3Value.z,
								time1.floatValue,
								val2.vector3Value.z,
								inSlope2.vector3Value.z,
								time2.floatValue,
								oneKeyframeTime);
							var vec3TanX = CalculateLinearTangent(
											val1.vector3Value.x,
											time1.floatValue,
											val2.vector3Value.x,
											time2.floatValue);
							var vec3TanY = CalculateLinearTangent(
											val1.vector3Value.y,
											time1.floatValue,
											val2.vector3Value.y,
											time2.floatValue);
							var vec3TanZ = CalculateLinearTangent(
											val1.vector3Value.z,
											time1.floatValue,
											val2.vector3Value.z,
											time2.floatValue);
							outSlope1.vector3Value = new Vector3(vec3TanX, vec3TanY, vec3TanZ);
							inSlope2.vector3Value  = new Vector3(vec3TanX, vec3TanY, vec3TanZ);
							break;

						case SerializedPropertyType.Vector4:
							IsLinearTargetTangent(
								"Vector4 - X - " + attr,
								val1.vector4Value.x,
								outSlope1.vector4Value.x,
								time1.floatValue,
								val2.vector4Value.x,
								inSlope2.vector4Value.x,
								time2.floatValue,
								oneKeyframeTime);
							IsLinearTargetTangent(
								"Vector4 - Y - " + attr,
								val1.vector4Value.y,
								outSlope1.vector4Value.y,
								time1.floatValue,
								val2.vector4Value.y,
								inSlope2.vector4Value.y,
								time2.floatValue,
								oneKeyframeTime);
							IsLinearTargetTangent(
								"Vector4 - Z - " + attr,
								val1.vector4Value.z,
								outSlope1.vector4Value.z,
								time1.floatValue,
								val2.vector4Value.z,
								inSlope2.vector4Value.z,
								time2.floatValue,
								oneKeyframeTime);
							IsLinearTargetTangent(
								"Vector4 - W - " + attr,
								val1.vector4Value.w,
								outSlope1.vector4Value.w,
								time1.floatValue,
								val2.vector4Value.w,
								inSlope2.vector4Value.w,
								time2.floatValue,
								oneKeyframeTime);
							var vec4TanX = CalculateLinearTangent(
											val1.vector4Value.x,
											time1.floatValue,
											val2.vector4Value.x,
											time2.floatValue);
							var vec4TanY = CalculateLinearTangent(
											val1.vector4Value.y,
											time1.floatValue,
											val2.vector4Value.y,
											time2.floatValue);
							var vec4TanZ = CalculateLinearTangent(
											val1.vector4Value.z,
											time1.floatValue,
											val2.vector4Value.z,
											time2.floatValue);
							var vec4TanW = CalculateLinearTangent(
											val1.vector4Value.w,
											time1.floatValue,
											val2.vector4Value.w,
											time2.floatValue);
							outSlope1.vector4Value = new Vector4(vec4TanX, vec4TanY, vec4TanZ, vec4TanW);
							inSlope2.vector4Value  = new Vector4(vec4TanX, vec4TanY, vec4TanZ, vec4TanW);
							break;

						case SerializedPropertyType.Quaternion:
							IsLinearTargetTangent(
								"Quaternion - X - " + attr,
								val1.quaternionValue.x,
								outSlope1.quaternionValue.x,
								time1.floatValue,
								val2.quaternionValue.x,
								inSlope2.quaternionValue.x,
								time2.floatValue,
								oneKeyframeTime);
							IsLinearTargetTangent(
								"Quaternion - Y - " + attr,
								val1.quaternionValue.y,
								outSlope1.quaternionValue.y,
								time1.floatValue,
								val2.quaternionValue.y,
								inSlope2.quaternionValue.y,
								time2.floatValue,
								oneKeyframeTime);
							IsLinearTargetTangent(
								"Quaternion - Z - " + attr,
								val1.quaternionValue.z,
								outSlope1.quaternionValue.z,
								time1.floatValue,
								val2.quaternionValue.z,
								inSlope2.quaternionValue.z,
								time2.floatValue,
								oneKeyframeTime);
							IsLinearTargetTangent(
								"Quaternion - W - " + attr,
								val1.quaternionValue.w,
								outSlope1.quaternionValue.w,
								time1.floatValue,
								val2.quaternionValue.w,
								inSlope2.quaternionValue.w,
								time2.floatValue,
								oneKeyframeTime);
							var qua4TanX = CalculateLinearTangent(
											val1.quaternionValue.x,
											time1.floatValue,
											val2.quaternionValue.x,
											time2.floatValue);
							var qua4TanY = CalculateLinearTangent(
											val1.quaternionValue.y,
											time1.floatValue,
											val2.quaternionValue.y,
											time2.floatValue);
							var qua4TanZ = CalculateLinearTangent(
											val1.quaternionValue.z,
											time1.floatValue,
											val2.quaternionValue.z,
											time2.floatValue);
							var qua4TanW = CalculateLinearTangent(
											val1.quaternionValue.w,
											time1.floatValue,
											val2.quaternionValue.w,
											time2.floatValue);
							outSlope1.quaternionValue = new Quaternion(qua4TanX, qua4TanY, qua4TanZ, qua4TanW);
							inSlope2.quaternionValue  = new Quaternion(qua4TanX, qua4TanY, qua4TanZ, qua4TanW);
							break;
					}
				}
			}
		}

		so_clip.ApplyModifiedPropertiesWithoutUndo();
	}


	private static bool IsLinearTargetTangent(
		string txt,
		float val1,
		float outSlope1,
		float time1,
		float val2,
		float inSlope2,
		float time2,
		float oneKeyframeTime)
	{
		if (txt.Contains("localEulerAnglesRaw") == false){ return false;}

		var outTangetDegree1_1 = Mathf.Rad2Deg * Mathf.Atan(outSlope1 * oneKeyframeTime);
		var inTangetDegree2_1  = Mathf.Rad2Deg * Mathf.Atan(inSlope2 * oneKeyframeTime);
		var AngleDiff_1        = Mathf.Abs(outTangetDegree1_1 - inTangetDegree2_1);
		var _time1             = time1 / oneKeyframeTime;
		// var outTangetDegree1_2 = Mathf.Rad2Deg * Mathf.Atan(outSlope1);
		// var inTangetDegree2_2  = Mathf.Rad2Deg * Mathf.Atan(inSlope2);
		// var AngleDiff_2        = Mathf.Abs(outTangetDegree1_2 - inTangetDegree2_2);
		var _time2             = time2 / oneKeyframeTime;
		Debug.Log("==================================================================================");
		Debug.Log(txt + " : Time1 = " + _time1 + " ( " + time1 + " ) - Time2 = " + _time2 + " ( " + time2 + " )");
		Debug.Log("outTangetDegree1_1 = " + outTangetDegree1_1 +
			"  :  inTangetDegree2_1 = " + inTangetDegree2_1 +
			"  :  AngleDiff_1 = " + AngleDiff_1);
		Debug.Log("val1 = " + val1 + " , val2 = " + val2);
		// Debug.Log("outTangetDegree1_2 = " + outTangetDegree1_2 +
		// 	"  :  inTangetDegree2_2 = " + inTangetDegree2_2 +
		// 	"  :  AngleDiff_2 = " + AngleDiff_2);

		// Y軸（2点間のValue値の差）、X軸（2点間の時間の差）を元に角度を求める
		var atan2 = Mathf.Rad2Deg * Mathf.Atan2(
			val2 * oneKeyframeTime - val1 * oneKeyframeTime,
			time2 * oneKeyframeTime - time1 * oneKeyframeTime);
		var atan2_2 = Mathf.Rad2Deg * Mathf.Atan2(val2 - val1, time2 - time1);

		// Debug.Log("atan2 = " + atan2 + "  :  atan2_2 = " + atan2_2);


		var p0 = new Vector2(time1, val1);
		var p3 = new Vector2(time2, val2);
		var partTime = (float)((time2 - time1) * 0.1);
		// Debug.Log();
		for (var i = 1; i <= 10; i++)
		{
			CalculateCurvePoint(p0, outTangetDegree1_1, p3, inTangetDegree2_1, 0.1f * i );
		}
		// if (Mathf.Abs(atan2) > 80 && Mathf.Abs(atan2_2) > 80)
		// {
		// 	Debug.LogWarning("80度を超えてるよーーーー");
		// 	return true;
		// }
		// if (Mathf.Abs(AngleDiff_1) > 80 || Mathf.Abs(AngleDiff_2) > 80)
		// {
		// 	Debug.LogWarning("80度を超えてるよーーーー");
		// 	return true;
		// }
		return false;
	}


/*
Flashゲーム講座＆ASサンプル集【角度の計算について】
https://hakuhin.jp/as/rotation.html#ROTATION_01


Flashゲーム講座＆ASサンプル集【曲線について】
https://hakuhin.jp/as/curve.html#CURVE_02
*/
	private static void CalculateCurvePoint(Vector2 p0, float p0_deg, Vector2 p3, float p3_deg, float t)
	{

		var p1     = Vector2.zero;
		var p2     = Vector2.zero;
		var result = Vector2.zero;

		p1.x = Mathf.Cos(p0_deg);
		p1.y = Mathf.Sin(p0_deg);
		p2.x = Mathf.Cos(p3_deg);
		p2.y = Mathf.Sin(p3_deg);

		var v = (1-t) * (1-t) * (1-t);
		result.x += v * p0.x;  
		result.y += v * p0.y;

		v = 3 * t * (1-t) * (1-t);
		result.x += v * p1.x;
		result.y += v * p1.y;

		v = 3 * t * t * (1-t);
		result.x += v * p2.x;
		result.y += v * p2.y;

		v = t * t * t;
		result.x += v * p3.x;
		result.y += v * p3.y;

		Debug.Log("result ( " + t + " )  : X = " + result.x + " , Y = " + result.y);
	}

	private static float CalculateLinearTangent(float val1, float time1, float val2, float time2)
	{
		float dt = time2 - time1;
		if (Mathf.Abs(dt) < float.Epsilon) { return 0.0f; }

		return (val2 - val1) / dt;
	} 
}