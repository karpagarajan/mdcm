#Code snipped to create a dicom file, once you've extracted the byte data from an image

# Details #
```
       /// <summary>
        /// Writes a dicom file from an array of image data and the width and height
        /// </summary>
        /// <param name="greybytes">byte array of image data, this example uses 2 bytes for each greyscale pixel</param>
        /// <param name="imgwidth">width in pixels</param>
        /// <param name="imgheight">height in pixels</param>
        public void MakeGreyDicom(byte[] greybytes, ushort imgwidth, ushort imgheight)
        {
            DcmUID studyUid = DcmUID.Generate();
            DcmUID seriesUid = DcmUID.Generate(studyUid, 1);
            DcmUID instUid = DcmUID.Generate(seriesUid, 1);
            DcmDataset data = new DcmDataset(DcmTS.ImplicitVRLittleEndian);
            data.AddElementWithValue(DcmTags.SOPClassUID, DcmUIDs.ComputedRadiographyImageStorage);
            //data.AddElementWithValue(DcmTags.SOPClassUID, DcmUIDs.SecondaryCapture); 
            data.AddElementWithValue(DcmTags.StudyInstanceUID, studyUid);
            data.AddElementWithValue(DcmTags.SeriesInstanceUID, seriesUid);
            data.AddElementWithValue(DcmTags.SOPInstanceUID, instUid);

            //data.AddElementWithValue(DcmTags.MediaStorageSOPClassUID, DcmUIDs.ImplicitVRLittleEndian);
            //data.AddElementWithValueString(DcmTags.MediaStorageSOPClassUID, DcmUIDs.ComputedRadiographyImageStorage.ToString());

            //type 2 attributes
            data.AddElementWithValueString(DcmTags.PatientID, "12345");
            data.AddElementWithValueString(DcmTags.PatientsName, "Doe^John");
            data.AddElementWithValueString(DcmTags.PatientsBirthDate, "00000000");
            data.AddElementWithValueString(DcmTags.PatientsSex, "M");
            data.AddElementWithValue(DcmTags.StudyDate, DateTime.Now);
            data.AddElementWithValue(DcmTags.StudyTime, DateTime.Now);
            data.AddElementWithValueString(DcmTags.AccessionNumber, "");
            data.AddElementWithValueString(DcmTags.ReferringPhysiciansName, "");
            data.AddElementWithValueString(DcmTags.StudyID, "1");
            data.AddElementWithValueString(DcmTags.SeriesNumber, "1");
            data.AddElementWithValueString(DcmTags.ModalitiesInStudy, "CR");
            data.AddElementWithValueString(DcmTags.Modality, "CR");
            data.AddElementWithValueString(DcmTags.NumberOfStudyRelatedInstances, "1");
            data.AddElementWithValueString(DcmTags.NumberOfStudyRelatedSeries, "1");
            data.AddElementWithValueString(DcmTags.NumberOfSeriesRelatedInstances, "1");
            data.AddElementWithValueString(DcmTags.PatientOrientation, "F/A");
            data.AddElementWithValueString(DcmTags.ImageLaterality, "U");

            ushort bitdepth = 2;

            DcmPixelData pixelData = new DcmPixelData(DcmTS.ImplicitVRLittleEndian);

            pixelData.ImageWidth = imgwidth;
            pixelData.ImageHeight = imgheight;
            pixelData.SamplesPerPixel = 1;

            pixelData.HighBit = 11;
            pixelData.BitsStored = 12;
            pixelData.BitsAllocated = 16;

            pixelData.PhotometricInterpretation = "MONOCHROME1";//2 byte gray?
            pixelData.PixelDataElement = DcmElement.Create(DcmTags.PixelData, DcmVR.OB); //OB: Other Byte, OW: Other Word

            //pixelData.AddFrame(bmpBytes);
            pixelData.AddFrame(greybytes);

            pixelData.UpdateDataset(data);
            DicomFileFormat ff = new DicomFileFormat(data);
            string fileout = Path.Combine(Directory.GetCurrentDirectory(), "greyimg.dcm");
            ff.Save(fileout, DicomWriteOptions.Default);
            ff = null;
        }
```