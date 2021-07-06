using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


/// <summary>
/// This class is built to manage the interactions between the different scripts that create a vector field. 
/// Given a FieldZone, it calls upon that for the positions of each vector to be plotted, then uses that 
/// to calculate the value of the buffer at each point. Finally, it plots these results with GPU instancing.
/// 
/// Does not currently support a changing number of points. 
/// </summary>
[RequireComponent(typeof(FieldZone), typeof(Display))]
public class VectorField : MonoBehaviour
{
    /// <summary>
    /// The <cref>FieldZone</cref> object used to determine positions.
    /// </summary>
    public FieldZone zone { get; set; }
    // Why is this not serializable? %%%%%%%%%%

    /// <summary>
    /// The buffer in which the vector worldspace positions are stored.
    /// </summary>
    public ComputeBuffer positionsBuffer { get; protected set; }
    /// <summary>
    /// The buffer in which the vector values are stored. 
    /// Same indexing scheme as <cref>positionsBuffer</cref>.
    /// </summary>
    public ComputeBuffer vectorsBuffer { get; protected set; }
    public ComputeBuffer magneticFieldPastBuffer { get; protected set; }

    public ComputeBuffer integrandPastBuffer { get; protected set; }
    public ComputeBuffer integrandBuffer { get; protected set; }
    /// <summary>
    /// Stores the extra float arguments used in the computation.
    /// Set your own indexing scheme and initialize by subscribing
    /// to the `preCalculations` delegate.
    /// </summary>
    public ComputeBuffer floatArgsBuffer { get; set; }
    /// <summary>
    /// Stores the extra vector arguments used in the computation.
    /// Set your own indexing scheme and initialize by subscribing
    /// to the `preCalculations` delegate.
    /// </summary>
    public ComputeBuffer vectorArgsBuffer { get; set; }
    

    /// <summary>
    /// The number of points at which vectors will be plotted and the number of values in each buffer.
    /// </summary>
    int numOfPoints;

    /// <summary>
    /// The compute shader used to generate the vector field. 
    /// </summary>
    [SerializeField]
    public ComputeShader computeShader;

    // Property IDs used to send values to various shaders.
    static readonly int
        centerID = Shader.PropertyToID("_CenterPosition"),
        positionsBufferID = Shader.PropertyToID("_Positions"),
        vectorBufferID = Shader.PropertyToID("_Vectors"),
        magneticFieldPastBufferID = Shader.PropertyToID("_MagneticFieldPast"),
        integrandPastBufferID = Shader.PropertyToID("_IntegrandPast"),
        floatArgsID = Shader.PropertyToID("_FloatArgs"),
        timePastID = Shader.PropertyToID("_TimePast"),
        timeNowID = Shader.PropertyToID("_TimeNow"),
        timeIntervalID = Shader.PropertyToID("_TimeInterval"),
        numberOfPointsID = Shader.PropertyToID("_NumberOfPoints"),
        integrandBufferID = Shader.PropertyToID("_Integrand"),
        distanceID = Shader.PropertyToID("_Distance"),
        vectorArgsID = Shader.PropertyToID("_VectorArgs");


    /// <summary>
    /// The possible types of field to display. 
    /// It is the user's responsibility to make sure that these selections align with those in FieldLibrary.hlsl
    /// </summary>
    public enum FieldType { Outwards, Swirl, Coulomb, Db, Electric}
    /// <summary>
    /// The type of field to be displayed. Cannot be changed in Play Mode if <cref>isDynamic</cref> is set to False.
    /// </summary>
    [SerializeField]
    public FieldType fieldType;

    /// <summary>
    /// Set this to true if the field values should be updated each frame. 
    /// Requires more GPU time. 
    /// 
    /// Tip: this can be toggled on during play mode to force the field to recalculate, then 
    /// toggled back off. 
    /// </summary>
    [SerializeField]
    bool isDynamic;
    /// <summary>
    /// Indicates whether the vectors buffer has been initialized. For non-dynamic fields. 
    /// </summary>
    private bool hasBeenCalculated;

    [SerializeField]
    public Display display { get; protected set; }


    public delegate void Reminder();
    /// <summary>
    /// This delegate will get called prior to setting the positions buffer.
    /// </summary>
    public Reminder preSetPositions;
    /// <summary>
    /// This delegate will get called prior to setting the vectors buffer. 
    /// </summary>
    public Reminder preCalculations;
    /// <summary>
    /// This delegate will get called prior to displaying the field. 
    /// </summary>
    public Reminder preDisplay;



    private int frameCount;

    private void Awake() {
        if (zone == null) {
            zone = GetComponent<FieldZone>();
        }
        if (display == null) {
            display = GetComponent<Display>();
        }
    }

    private void OnEnable()
    {
        preSetPositions += Pass;
        preCalculations += Pass;
        preDisplay += Pass;


        preSetPositions();

        zone.SetPositions();

        positionsBuffer = zone.positionBuffer;
        numOfPoints = positionsBuffer.count;
        computeShader.SetInt(numberOfPointsID, numOfPoints);
        unsafe // <-- This could maybe be a source of problems.
        {
            vectorsBuffer = new ComputeBuffer(numOfPoints, sizeof(Vector3)); // last arg: size of single object
            magneticFieldPastBuffer = new ComputeBuffer(numOfPoints, sizeof(Vector3));
            integrandPastBuffer = new ComputeBuffer(numOfPoints, sizeof(Vector3));
            integrandBuffer = new ComputeBuffer(numOfPoints, sizeof(Vector3));
        }

        
    }



    private void OnDisable()
    {
        vectorsBuffer.Release();
        vectorsBuffer = null;

        magneticFieldPastBuffer.Release();
        magneticFieldPastBuffer = null;

        integrandPastBuffer.Release();
        integrandPastBuffer = null;
        
        integrandBuffer.Release();
        integrandBuffer = null;
    }



    // Update is called once per frame
    void Update()
    {
        
        preSetPositions();
        zone.SetPositions();
        
        computeShader.SetFloat(timeIntervalID, Time.smoothDeltaTime);
        

        if (zone.canMove) {
            isDynamic = true;
        }

        
        if(isDynamic || !hasBeenCalculated)
        {
            preCalculations();
            CalculateVectors();
            hasBeenCalculated = true;

            display.maxVectorLength = zone.maxVectorLength;
            display.bounds = zone.bounds;
        }

        // Debug code
        //Vector3[] debugArray = new Vector3[numOfPoints];
        //vectorsBuffer.GetData(debugArray);
        //Debug.Log((("First three points in vector array: " + debugArray[0]) + debugArray[1]) + debugArray[2]);
        //Debug.Log((("Last three points in vector array: " + debugArray[numOfPoints - 1]) + debugArray[numOfPoints - 2]) + debugArray[numOfPoints - 3]);
    }

    private void LateUpdate() // WHAT REQUIRES THIS? %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    {
        preDisplay();

        display.DisplayVectors(positionsBuffer, vectorsBuffer);
    }


    /// <summary>
    /// Interfaces with the <cref>computeShader</cref> and calculates the value of the vectors at each point, storing them in the buffers. 
    /// </summary>
    private void CalculateVectors()
    {
        // The data is sent to the computeShader for calculation
        computeShader.SetVector(centerID, zone.fieldOrigin);

        int kernelID = (int)fieldType;
        computeShader.SetBuffer(kernelID, positionsBufferID, positionsBuffer);
        Vector3[] debugArray = new Vector3[numOfPoints];
        positionsBuffer.GetData(debugArray);
        //Debug.Log((("First three points in POSITIONS BUFFER: " + debugArray[60].x) + " " + debugArray[60].y) + " " + debugArray[60].z);
        
        computeShader.SetBuffer(kernelID, vectorBufferID, vectorsBuffer);
        Vector3[] debugArray2 = new Vector3[numOfPoints];
        vectorsBuffer.GetData(debugArray2);
        Debug.Log((("First three points in OUTWARDS BUFFER: " + debugArray2[60].x) + " " + debugArray2[60].y) + " " + debugArray2[60].z);

        if(floatArgsBuffer != null) {
            //Debug.Log("floatArgsBuffer");
            computeShader.SetBuffer(kernelID, floatArgsID, floatArgsBuffer);
        }
        // else{
        //      Debug.Log("floatArgsBuffer");
        // }

        if(vectorArgsBuffer != null) {
            computeShader.SetBuffer(kernelID, vectorArgsID, vectorArgsBuffer);
            // Vector3[] debugArray = new Vector3[numOfPoints];
            // vectorsBuffer.GetData(debugArray);
            // Debug.Log((("First three points: " + debugArray[615].x) + " " + debugArray[615].y) + " " + debugArray[615].z);
        }
        
        if (magneticFieldPastBuffer != null){
            computeShader.SetBuffer(kernelID, magneticFieldPastBufferID, magneticFieldPastBuffer);
            // Vector3[] debugArray = new Vector3[numOfPoints];
            // magneticFieldPastBuffer.GetData(debugArray);
            // Debug.Log((("First three points: " + debugArray[615].x) + " " + debugArray[615].y) + " " + debugArray[615].z);
        }

        if (integrandPastBuffer != null){
            computeShader.SetBuffer(kernelID, integrandPastBufferID, integrandPastBuffer);
            // Vector3[] debugArray = new Vector3[numOfPoints];
            // vectorsBuffer.GetData(debugArray);
            // Debug.Log((("First three points in integrandPastBuffer: " + debugArray[0].x) + " " + debugArray[0].y) + " " + debugArray[0].z);
        }

        if (integrandBuffer != null){
            computeShader.SetBuffer(kernelID, integrandBufferID, integrandBuffer);
        }

        // if(kernelID == (int)FieldType.Db){
        //     computeShader.SetBuffer(kernelID, magneticFieldPastBufferID, magneticFieldPastBuffer);
            
            
            
        //     // if (frameCount % 10 == 0){
        //     //     computeShader.SetBuffer(kernelID, vectorPastBufferID, vectorsPastBuffer);
        //     // }
        // }

        // This does the math and stores information in the positionsBuffer.
        int groups = Mathf.CeilToInt(numOfPoints / 64f);
        computeShader.Dispatch(kernelID, groups, 1, 1);
    }

    public void Pass()
    {
        ;
    }

    private void OnDrawGizmos()
    {
        if(display != null && display.bounds != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(display.bounds.center, display.bounds.size);
        }
    }
}
